"""Small startup/update requests using the existing HTTPS client transport."""
import json
import os
from pathlib import Path
from bear_cache_client import BearCache, CacheUnavailable


def run(file):
    target = Path(file)
    args = json.loads(target.read_text(encoding='utf-8-sig'))
    client = BearCache(args['cache'], os.environ.get('BEAR_DOWNLOAD_PROXY', ''))
    try:
        if args['action'] == 'update':
            reply = client.request('GET', '/v1/client/update').json()
        else:
            client.identify()
            reply = client.request('POST', '/v1/client/access', json_body={
                'hwid': args['hwid'], 'client_version': args['client_version']}).json()
    except CacheUnavailable:
        print(json.dumps({'error': 'client_service_unavailable', 'message': '暂时无法连接验证服务，请检查网络后重试。'}), flush=True)
        return 1
    temporary = target.with_suffix('.reply')
    temporary.write_text(json.dumps(reply, ensure_ascii=False), encoding='utf-8')
    os.replace(temporary, target)
    return 0
