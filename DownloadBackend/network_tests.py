"""Local TLS fixtures for address routing; no Steam account or external requests."""
from contextlib import contextmanager
from datetime import datetime, timedelta, timezone
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
import ssl
import tempfile
import threading
import unittest
from unittest.mock import Mock, patch
import io
import zipfile

from cryptography import x509
from cryptography.hazmat.primitives import hashes, serialization
from cryptography.hazmat.primitives.asymmetric import rsa
from cryptography.x509.oid import NameOID, ExtendedKeyUsageOID
import requests

import steam_network as network
import steam_manifest as core

HOST = 'cdn.akamai.steamstatic.com'


@contextmanager
def endpoint(cert_host=HOST):
    with tempfile.TemporaryDirectory() as directory:
        folder = Path(directory)
        now = datetime.now(timezone.utc)
        key = rsa.generate_private_key(public_exponent=65537, key_size=2048)
        ca_name = x509.Name([x509.NameAttribute(NameOID.COMMON_NAME, 'Bear network fixture')])
        ca = (x509.CertificateBuilder().subject_name(ca_name).issuer_name(ca_name)
              .public_key(key.public_key()).serial_number(x509.random_serial_number())
              .not_valid_before(now-timedelta(minutes=1)).not_valid_after(now+timedelta(days=1))
              .add_extension(x509.BasicConstraints(ca=True, path_length=None), critical=True)
              .add_extension(x509.SubjectKeyIdentifier.from_public_key(key.public_key()), critical=False)
              .add_extension(x509.KeyUsage(digital_signature=True, content_commitment=False,
                  key_encipherment=False, data_encipherment=False, key_agreement=False,
                  key_cert_sign=True, crl_sign=True, encipher_only=False, decipher_only=False), critical=True)
              .sign(key, hashes.SHA256()))
        leaf = (x509.CertificateBuilder()
                .subject_name(x509.Name([x509.NameAttribute(NameOID.COMMON_NAME, cert_host)]))
                .issuer_name(ca_name).public_key(key.public_key())
                .serial_number(x509.random_serial_number())
                .not_valid_before(now-timedelta(minutes=1)).not_valid_after(now+timedelta(days=1))
                .add_extension(x509.BasicConstraints(ca=False, path_length=None), critical=True)
                .add_extension(x509.AuthorityKeyIdentifier.from_issuer_public_key(key.public_key()), critical=False)
                .add_extension(x509.ExtendedKeyUsage([ExtendedKeyUsageOID.SERVER_AUTH]), critical=False)
                .add_extension(x509.SubjectAlternativeName([x509.DNSName(cert_host)]), critical=False)
                .sign(key, hashes.SHA256()))
        (folder/'ca.pem').write_bytes(ca.public_bytes(serialization.Encoding.PEM))
        (folder/'leaf.pem').write_bytes(leaf.public_bytes(serialization.Encoding.PEM))
        (folder/'key.pem').write_bytes(key.private_bytes(serialization.Encoding.PEM,
            serialization.PrivateFormat.PKCS8, serialization.NoEncryption()))
        observed = dict(sni=[], hosts=[])

        class Handler(BaseHTTPRequestHandler):
            protocol_version = 'HTTP/1.1'

            def handle(self):
                try:
                    super().handle()
                except ConnectionResetError:
                    pass  # Expected when a fixture client rejects the certificate.

            def do_GET(self):
                observed['hosts'].append(self.headers['Host'])
                self.send_response(200)
                self.send_header('Content-Length', '5')
                self.end_headers()
                self.wfile.write(b'chunk')

            def log_message(self, *args):
                pass

        server = ThreadingHTTPServer(('127.0.0.1', 0), Handler)
        context = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
        context.load_cert_chain(folder/'leaf.pem', folder/'key.pem')
        context.set_servername_callback(lambda sock, name, ctx: observed['sni'].append(name))
        server.socket = context.wrap_socket(server.socket, server_side=True)
        thread = threading.Thread(target=server.serve_forever, daemon=True)
        thread.start()
        try:
            yield server.server_port, str(folder/'ca.pem'), observed
        finally:
            server.shutdown()
            server.server_close()
            thread.join()


class NetworkTests(unittest.TestCase):
    def test_chunk_fallback_reaches_last_node_without_repeating_failed_nodes(self):
        hosts = ['node%d.steamcontent.com' % n for n in range(6)]
        archive = io.BytesIO()
        with zipfile.ZipFile(archive, 'w') as output:
            output.writestr('z', b'fixture chunk')
        called = []

        def get(url, **kwargs):
            host = network.urlsplit(url).hostname
            called.append(host)
            if host != hosts[-1]:
                raise requests.ConnectionError('unavailable node')
            return archive.getvalue()

        with patch.object(core, 'CDN_HOSTS', hosts), patch.object(core, '_host_bad', {}), \
                patch.object(core, '_host_ok', {}), patch.object(core, '_rr', [0]), \
                patch.object(core, 'http_get', side_effect=get):
            body, host = core.fetch_chunk(1, 'fixture', '', '')
        self.assertEqual((body, host), (b'fixture chunk', hosts[-1]))
        self.assertEqual(set(called), set(hosts))
        self.assertEqual(len(called), len(hosts))

    def client(self, port, ca, local_sni=False):
        client = network.SteamHTTP(local_sni=local_sni)
        client.routes = {HOST: '127.0.0.1'}
        normal, routed = client.sessions()
        for session in (normal, routed, client.local.sni):
            session.trust_env = False
            session.verify = ca
            self.addCleanup(session.close)
        routed.mount('https://%s:%d/' % (HOST, port), network.AddressAdapter('127.0.0.1'))
        client.local.sni.mount('https://%s:%d/' % (HOST, port), network.AddressAdapter('127.0.0.1', suppress_sni=True))
        normal.get = Mock(side_effect=requests.ConnectionError('fixture DNS failure'))
        return client, normal, routed

    @patch.object(network, 'get_environ_proxies', return_value={})
    def test_local_sni_omits_domain_but_verifies_original_host_and_reuses_connection(self, env):
        with endpoint() as (port, ca, observed):
            client, normal, routed = self.client(port, ca, local_sni=True)
            url = 'https://%s:%d/depot/1/chunk/test' % (HOST, port)
            self.assertEqual(client.get(url, (2, 2)).content, b'chunk')
            self.assertEqual(client.get(url, (2, 2)).content, b'chunk')
            self.assertEqual(normal.get.call_count, 1)
            self.assertEqual(observed['sni'], [None])
            self.assertEqual(observed['hosts'], ['%s:%d' % (HOST, port)] * 2)
            self.assertIn(HOST, client.sni_hosts)
            client.local.sni.close()

    @patch.object(network, 'get_environ_proxies', return_value={})
    def test_local_sni_rejects_wrong_hostname_and_tries_normal_tls(self, env):
        with endpoint('wrong.invalid') as (port, ca, observed):
            client, normal, routed = self.client(port, ca, local_sni=True)
            with self.assertRaisesRegex(requests.exceptions.SSLError, 'Hostname mismatch|hostname'):
                client.get('https://%s:%d/' % (HOST, port), (2, 2))
            self.assertEqual(observed['sni'], [None, HOST])
            self.assertEqual(observed['hosts'], [])
            self.assertIn(HOST, client.sni_unavailable)

    @patch.object(network, 'get_environ_proxies', return_value={})
    def test_local_sni_can_use_dns_when_no_address_hint_exists(self, env):
        from urllib3.util import connection
        original = connection.create_connection
        with endpoint() as (port, ca, observed):
            client, normal, routed = self.client(port, ca, local_sni=True)
            client.routes.clear()
            client.local.sni.mount('https://%s:%d/' % (HOST, port), network.AddressAdapter(None, suppress_sni=True))

            def connect(address, *args, **kwargs):
                return original(('127.0.0.1', address[1]), *args, **kwargs)

            with patch.object(connection, 'create_connection', side_effect=connect):
                response = client.get('https://%s:%d/' % (HOST, port), (2, 2))
            self.assertEqual(response.content, b'chunk')
            self.assertEqual(observed['sni'], [None])
            client.local.sni.close()

    @patch.object(network, 'get_environ_proxies', return_value={})
    def test_ip_fallback_preserves_sni_host_certificate_and_connection_reuse(self, env):
        with endpoint() as (port, ca, observed):
            client, normal, routed = self.client(port, ca)
            url = 'https://%s:%d/depot/1/chunk/test' % (HOST, port)
            self.assertEqual(client.get(url, (2, 2)).content, b'chunk')
            self.assertEqual(client.get(url, (2, 2)).content, b'chunk')
            self.assertEqual(normal.get.call_count, 1)
            self.assertEqual(observed['sni'], [HOST])
            self.assertEqual(observed['hosts'], ['%s:%d' % (HOST, port)] * 2)
            routed.close()

    @patch.object(network, 'get_environ_proxies', return_value={})
    def test_ip_fallback_rejects_wrong_hostname_certificate(self, env):
        with endpoint('wrong.invalid') as (port, ca, observed):
            client, normal, routed = self.client(port, ca)
            with self.assertRaisesRegex(requests.exceptions.SSLError, 'Hostname mismatch|hostname'):
                client.get('https://%s:%d/' % (HOST, port), (2, 2))
            self.assertEqual(observed['hosts'], [])

    def test_manual_proxy_failure_does_not_fall_back_to_direct(self):
        client = network.SteamHTTP(proxy='http://127.0.0.1:7890')
        client.routes = {HOST: '127.0.0.1'}
        normal, routed = client.sessions()
        normal.get = Mock(side_effect=requests.ConnectionError('proxy unavailable'))
        routed.get = Mock()
        with self.assertRaises(requests.ConnectionError):
            client.get('https://' + HOST + '/', (2, 2))
        routed.get.assert_not_called()
        normal.close()
        routed.close()

    @patch.object(network, 'get_environ_proxies', return_value={'https': 'http://127.0.0.1:7890'})
    def test_system_proxy_failure_does_not_fall_back_to_direct(self, env):
        client = network.SteamHTTP()
        client.routes = {HOST: '127.0.0.1'}
        normal, routed = client.sessions()
        normal.get = Mock(side_effect=requests.ConnectionError('proxy unavailable'))
        routed.get = Mock()
        with self.assertRaises(requests.ConnectionError):
            client.get('https://' + HOST + '/', (2, 2))
        routed.get.assert_not_called()
        normal.close()
        routed.close()

    @patch.object(network, 'get_environ_proxies', return_value={})
    def test_http_denial_and_rate_limit_do_not_trigger_ip_switch(self, env):
        client = network.SteamHTTP()
        client.routes = {HOST: '127.0.0.1'}
        normal, routed = client.sessions()
        routed.get = Mock()
        for status in (403, 404, 429):
            response = requests.Response()
            response.status_code = status
            normal.get = Mock(return_value=response)
            self.assertEqual(client.get('https://' + HOST + '/', (2, 2)).status_code, status)
        routed.get.assert_not_called()
        normal.close()
        routed.close()


if __name__ == '__main__':
    unittest.main()
