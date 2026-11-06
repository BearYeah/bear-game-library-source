"""Bear download worker: manifest parsing, chunk download and resume."""
import argparse
from contextlib import contextmanager
import concurrent.futures as futures
from collections import Counter
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import sys
import time
import threading
import urllib.error

import requests
import steam_manifest as core
from download_catalog import select_depots, supplement_depots, merge_dlc_depots
from bear_cache_client import BearCache, CacheUnavailable, DailyLimit, DEFAULT_SERVER
from steam_network import SteamHTTP
from steam_cdn import discover, ContentPool, TransferMeter, AdaptiveConcurrency, CdnAuthorization

if hasattr(sys.stdout, "reconfigure"):
    sys.stdout.reconfigure(encoding="utf-8")
    sys.stderr.reconfigure(encoding="utf-8")


_event_lock = threading.Lock()

def emit(event, **data):
    with _event_lock:
        print(json.dumps(dict(event=event, **data), ensure_ascii=False), flush=True)


transfer_meter = None
content_get = None


def configure_network(proxy, route_file='', local_sni=True, download_proxy='direct'):
    global transfer_meter, content_get
    client = SteamHTTP(route_file, proxy, core.UA, local_sni)
    content = SteamHTTP(route_file, proxy if download_proxy == 'inherit' else '', core.UA,
                        local_sni, direct=download_proxy == 'direct')
    transfer_meter = TransferMeter()

    def request(transport, url, timeout=30, retries=2, raw=False, minimum_rate=0):
        chunk = '/chunk/' in url
        for attempt in range(retries):
            try:
                started = time.monotonic()
                with transport.get(url, timeout=(min(timeout, 3 if chunk else 5), timeout), stream=chunk) as response:
                    if response.status_code >= 400:
                        raise urllib.error.HTTPError(url, response.status_code,
                                                     response.reason, {}, None)
                    if chunk:
                        parts = []
                        received = 0
                        for part in response.iter_content(65536):
                            transfer_meter.add(len(part))
                            parts.append(part)
                            received += len(part)
                            elapsed = time.monotonic()-started
                            if elapsed > timeout*2 or (elapsed > 3 and received/elapsed < minimum_rate):
                                raise requests.Timeout('内容节点传输过慢，正在切换连接。')
                        return b''.join(parts)
                    return response.content if raw else response.content.decode("utf-8", "replace")
            except urllib.error.HTTPError:
                raise
            except requests.RequestException:
                if attempt + 1 == retries:
                    raise
                time.sleep(0.8)
    def get(url, **kwargs):
        return request(content if '/chunk/' in url else client, url, **kwargs)

    content_get = lambda url, **kwargs: request(content, url, **kwargs)
    core.http_get = get


def configure_content(job):
    emit('stage', phase='nodes', message='正在选择适合当前网络的内容节点…')
    try:
        nodes = discover(content_get, job['steam'], job['appid'])
    except (requests.RequestException, urllib.error.URLError, ValueError, KeyError):
        nodes = []
    authorization = CdnAuthorization(job['appid'], job['steam'], content_get,
        lambda message: emit('stage', phase='nodes', message=message))
    pool = ContentPool(nodes, ['https://'+host for host in core.CDN_HOSTS], content_get,
                       authorization=authorization)
    core.chunk_fetch = pool.fetch
    emit('stage', phase='nodes', message='内容连接已就绪 · '+('游戏内容直连' if job.get('download_proxy', 'direct') == 'direct' else '游戏内容跟随代理设置'))


def read_vdf(text):
    tokens = [m for m in re.finditer(r'"(?:\\.|[^"\\])*"|//[^\n]*|[{}]', text)
              if not m.group().startswith("//")]
    spans = {}
    index = 0

    def value(token):
        return re.sub(r'\\(["\\])', r'\1', token.group()[1:-1])

    def block(prefix):
        nonlocal index
        result = {}
        while index < len(tokens) and tokens[index].group() != "}":
            name = value(tokens[index])
            index += 1
            if tokens[index].group() == "{":
                opening = tokens[index].start()
                index += 1
                result[name] = block(prefix + (name,))
                spans[prefix + (name,)] = (opening, tokens[index].end())
                index += 1
            else:
                result[name] = value(tokens[index])
                index += 1
        return result
    return block(()), spans


def steam_node(tree):
    return tree["InstallConfigStore"]["Software"]["Valve"]["Steam"]


def quote(text):
    return '"' + str(text).replace('\\', '\\\\').replace('"', '\\"') + '"'


def vdf_body(values, depth):
    lines = []
    for key, value in values.items():
        prefix = "\t" * depth
        if isinstance(value, dict):
            lines.extend([prefix + quote(key), prefix + "{",
                          vdf_body(value, depth + 1), prefix + "}"])
        else:
            lines.append(prefix + quote(key) + "\t\t" + quote(value))
    return "\n".join(lines)


def merge_keys(config, keys):
    text = config.read_text(encoding="utf-8", errors="surrogateescape")
    tree, spans = read_vdf(text)
    node = steam_node(tree)
    depots = node.setdefault("depots", {})
    for depot, key in keys.items():
        depots.setdefault(str(depot), {})["DecryptionKey"] = key
    prefix = ("InstallConfigStore", "Software", "Valve", "Steam")
    if prefix + ("depots",) in spans:
        start, end = spans[prefix + ("depots",)]
        replacement = "{\n" + vdf_body(depots, 5) + "\n\t\t\t\t}"
        text = text[:start] + replacement + text[end:]
    else:
        _, end = spans[prefix]
        insertion = '\t"depots"\n\t\t\t\t{\n' + vdf_body(depots, 5) + '\n\t\t\t\t}\n\t\t\t'
        text = text[:end - 1] + insertion + text[end - 1:]
    backup = config.with_name(config.name + ".bear-download-" + time.strftime("%Y%m%d-%H%M%S") + "-%d.bak" % time.time_ns())
    shutil.copy2(config, backup)
    temporary = config.with_suffix(".vdf.bear-tmp")
    temporary.write_text(text, encoding="utf-8", errors="surrogateescape")
    os.replace(temporary, config)
    backups = sorted(config.parent.glob(config.name + ".bear-download-*.bak"), key=lambda p: (p.stat().st_mtime_ns, p.name), reverse=True)
    for old in backups[5:]:
        old.unlink()


@contextmanager
def waiting(message, phase="metadata"):
    stop = threading.Event()
    started = time.monotonic()
    emit("stage", phase=phase, message=message)
    def report():
        while not stop.wait(1):
            emit("stage", phase=phase, message=message + "（已等待 %d 秒）" % (time.monotonic()-started))
    thread = threading.Thread(target=report, daemon=True)
    thread.start()
    try:
        yield
    finally:
        stop.set()
        thread.join()


def download_selection(job):
    return dict(dlc_ids=sorted({int(appid) for appid in job.get("dlc_ids", [])}),
                dlc_only=bool(job.get("dlc_only", False)),
                install_dir=str(Path(job["install_dir"]).resolve()) if job.get("install_dir") else "")



def resolve_plan(job):
    appid = job["appid"]
    selection = download_selection(job)
    selection_args = dict(dlc_ids=selection["dlc_ids"]) if selection["dlc_ids"] else {}
    if selection["dlc_only"] and not selection["dlc_ids"]:
        raise RuntimeError("请先选择需要下载的 DLC。")
    server = None
    server_reply = None
    if os.environ.get("BEAR_DOWNLOAD_SERVER", DEFAULT_SERVER):
        server = BearCache(job["cache"], os.environ.get("BEAR_DOWNLOAD_PROXY", ""))
        try:
            with waiting("正在查询下载资料…"):
                server_reply = server.resolve(appid, job["language"], **selection_args)
        except DailyLimit:
            raise
        except CacheUnavailable:
            emit("stage", phase="metadata", message="正在切换可用下载资料…")
    info = server_reply.get("info") if server_reply else None
    if not info:
        with waiting("正在读取游戏版本…"):
            directory = os.environ.get("BEAR_METADATA_DIR", "")
            metadata_file = Path(directory) / (str(appid) + ".info.json")
            info = json.loads(metadata_file.read_text(encoding="utf-8")) if directory and metadata_file.is_file() else None
        if not info:
            raise RuntimeError("未找到 AppID %d 的公开游戏资料，请确认 AppID 是否正确。" % appid)
    dlc_info = {}
    for dlc_appid in selection["dlc_ids"]:
        if any(str(item.get("dlcappid", "")) == str(dlc_appid)
               for depot, item in info.get("depots", {}).items() if depot.isdigit()):
            continue
        directory = os.environ.get("BEAR_METADATA_DIR", "")
        metadata_file = Path(directory) / (str(dlc_appid) + ".info.json")
        dlc_info[dlc_appid] = json.loads(metadata_file.read_text(encoding="utf-8")) if directory and metadata_file.is_file() else {}
        merge_dlc_depots(info, dlc_info[dlc_appid], dlc_appid, appid)

    def choose():
        for dlc_appid, metadata in dlc_info.items():
            merge_dlc_depots(info, metadata, dlc_appid, appid)
        result = select_depots(info, job["language"], selection["dlc_ids"], selection["dlc_only"])
        owners = {str(item.get("dlcappid", "")) for item, _ in result.values()}
        missing = [str(dlc_appid) for dlc_appid in selection["dlc_ids"] if str(dlc_appid) not in owners]
        if missing:
            raise RuntimeError("所选 DLC 暂不能下载：%s（没有适用的独立 Windows public 内容）。" % ", ".join(missing))
        return result

    chosen = choose()
    if not chosen:
        raise RuntimeError("没有找到可下载的 Windows public 内容。")
    config = Path(job["steam"]) / "config" / "config.vdf"
    local, _ = read_vdf(config.read_text(encoding="utf-8", errors="surrogateescape"))
    local_keys = steam_node(local).get("depots", {})
    keys = {int(d): row["DecryptionKey"] for d, row in local_keys.items()
            if d.isdigit() and isinstance(row, dict) and "DecryptionKey" in row}
    if server_reply and server_reply["status"] == "ready":
        keys.update({int(d): k for d,k in server_reply["keys"].items()})
    supplement_tried = False
    supplement_error = ""
    requested_manifests = set()

    def load_supplement():
        nonlocal server_reply, supplement_tried, supplement_error
        if supplement_tried or server is None:
            return server_reply
        supplement_tried = True
        try:
            server_reply = server.prepare(appid, job["language"],
                lambda seconds:emit("stage", phase="manifest", message="正在补充下载资料（已等待 %d 秒）…" % seconds), **selection_args)
        except CacheUnavailable as error:
            supplement_error = str(error)
        else:
            keys.update({int(d): k for d,k in server_reply["keys"].items()})
        return server_reply

    def supplement_manifest(depot, gid):
        reply = load_supplement()
        if not reply or reply["status"] != "ready" or reply['manifests'].get(str(depot)) != str(gid):
            return None
        if (depot,gid) in requested_manifests:
            return None
        requested_manifests.add((depot,gid))
        try:
            with waiting("正在读取内容 %d 的清单…" % depot, "manifest"):
                return server.manifest(appid, depot, gid)
        except CacheUnavailable:
            return None

    if any(depot not in keys for depot in chosen):
        with waiting("正在查找内容解密资料…"):
            lua, _ = core.fetch_lua(appid, budget=25)
            if lua:
                keys.update(core.parse_lua(lua)["keys"])
            if any(depot not in keys for depot in chosen):
                key_vdf, _ = core.fetch_keyvdf(appid, budget=15)
                if key_vdf:
                    remote, _ = read_vdf(key_vdf)
                    keys.update({int(d): row["DecryptionKey"] for d, row in remote.get("depots", {}).items()
                                 if isinstance(row, dict) and "DecryptionKey" in row})
    if any(depot not in keys for depot in chosen):
        reply = load_supplement()
        if reply and reply["status"] == "ready":
            info = reply['info']
            chosen = choose()
    missing = [str(d) for d in chosen if d not in keys]
    if missing:
        dlc_missing = sorted({str(chosen[int(depot)][0]["dlcappid"]) for depot in missing
                              if chosen[int(depot)][0].get("dlcappid")})
        if dlc_missing:
            raise RuntimeError("所选 DLC 暂不能下载：%s（缺少内容 %s 的解密资料）。" %
                               (", ".join(dlc_missing), ", ".join(missing)))
        raise RuntimeError(supplement_error or "暂时没有这些内容的解密资料：" + ", ".join(missing))
    game_name = info.get("common", {}).get("name") or job["name"] or str(appid)
    install_name = info.get("config", {}).get("installdir") or re.sub(r'[\\/:*?"<>|]', "_", game_name)
    common = (Path(job["library"]) / "steamapps" / "common").resolve()
    destination = Path(selection["install_dir"]) if selection["install_dir"] else (common / install_name).resolve()
    if selection["dlc_only"] and not destination.is_dir():
        raise RuntimeError("下载 DLC 前，请先选择已安装的本体游戏目录。")
    if not selection["install_dir"] and (not destination.is_relative_to(common) or destination == common):
        raise RuntimeError("游戏安装目录无效。")
    depots = {}
    for depot, (_, public) in chosen.items():
        gid = int(public["gid"])
        local_manifest = Path(core.manifest_cache_path(depot, gid))
        blob = local_manifest.read_bytes() if local_manifest.is_file() else None
        source = "cache"
        if blob is None and server_reply and server_reply['status'] == 'ready' and server_reply['manifests'].get(str(depot)) == str(gid):
            requested_manifests.add((depot,gid))
            try:
                with waiting("正在读取内容 %d 的清单…" % depot, "manifest"):
                    blob = server.manifest(appid, depot, gid)
                source = 'remote'
            except CacheUnavailable:
                pass
        if blob is not None:
            if source != 'cache':
                core._save_manifest(str(local_manifest), blob)
        else:
            try:
                with waiting("正在获取内容 %d 的清单…" % depot, "manifest"):
                    blob, _ = core.fetch_manifest(depot, gid,
                        alternate=supplement_manifest if server else None)
                source = 'public'
            except (RuntimeError, OSError, requests.RequestException) as error:
                owner = chosen[depot][0].get("dlcappid")
                if owner:
                    raise RuntimeError("所选 DLC %s 暂不能下载：内容 %d 缺少版本 %d 的清单。" % (owner, depot, gid)) from error
                raise RuntimeError(supplement_error or "暂时无法取得所需清单，请稍后重试。") from error
        try:
            manifest = core.parse_manifest(blob, keys[depot])
        except (RuntimeError, ValueError) as error:
            owner = chosen[depot][0].get("dlcappid")
            if owner:
                raise RuntimeError("所选 DLC %s 暂不能下载：内容 %d 的清单与解密资料不匹配。" % (owner, depot)) from error
            raise
        emit("stage", phase="manifest", message="内容 %d 的清单已准备好。" % depot)
        depots[depot] = dict(gid=gid, key=keys[depot], blob=blob, manifest=manifest, source=source,
                             dlcappid=int(chosen[depot][0].get("dlcappid") or 0))
    build = (info.get("depots", {}).get("branches", {}).get("public") or {}).get("buildid") or "0"
    plan = dict(appid=appid, name=game_name, installdir=install_name, destination=destination,
                buildid=build, depots=depots, language=next((item.get("config", {}).get("language") for item, _ in chosen.values() if item.get("config", {}).get("language")), job["language"]),
                **selection)
    if selection["dlc_ids"]:
        emit("stage", phase="manifest", message="所选 DLC 的清单与解密资料已就绪：" +
             ", ".join(str(dlc_appid) for dlc_appid in selection["dlc_ids"]))
    if server and not selection["dlc_only"] and (not server_reply or server_reply["status"] != "ready"):
        try:
            with waiting("正在登记已取得的下载资料…"):
                if not server.token:
                    server.identify()
                server.finish(plan, info)
        except DailyLimit:
            raise
        except CacheUnavailable:
            emit("stage", phase="metadata", message="下载资料已就绪，正在准备传输…")
    return plan



def save_state(file, state):
    temporary = file.with_suffix(".tmp")
    temporary.write_text(json.dumps(state), encoding="utf-8")
    os.replace(temporary, file)


def download_plan(plan, state_file, jobs):
    concurrency = AdaptiveConcurrency(jobs)
    root = plan["destination"]
    root.mkdir(parents=True, exist_ok=True)
    identity = dict(path=str(root), depots={str(d): row["gid"] for d, row in plan["depots"].items()})
    if plan.get("dlc_ids") or plan.get("dlc_only"):
        identity.update(dlc_ids=plan.get("dlc_ids", []), dlc_only=plan.get("dlc_only", False))
    state = json.loads(state_file.read_text(encoding="utf-8")) if state_file.exists() else {}
    journal_file = state_file.with_suffix(".jsonl")
    completed = set(state.get("completed", [])) if state.get("identity") == identity else set()
    migrate_legacy = bool(completed) and not journal_file.exists()
    if state.get("identity") == identity and journal_file.exists():
        with journal_file.open("r+b") as journal:
            position = 0
            for line in journal:
                try:
                    completed.add(json.loads(line)["key"])
                    position += len(line)
                except json.JSONDecodeError:
                    journal.truncate(position)
                    break
    else:
        journal_file.parent.mkdir(parents=True, exist_ok=True)
        journal_file.write_text("", encoding="utf-8")
    groups = []
    all_tasks = []
    for depot, row in plan["depots"].items():
        files, tasks = [], []
        for file in row["manifest"]["files"]:
            target = (root / file["path"].replace("\\", "/")).resolve()
            if not target.is_relative_to(root):
                raise RuntimeError("内容清单中的文件路径超出游戏目录。")
            files.append((target, file))
            existed = target.is_file() if completed else False
            for chunk in file["chunks"]:
                key = "%d/%d/%s/%s/%s" % (depot, row["gid"], file["path"], chunk["offset"], chunk["sha"])
                if not existed:
                    completed.discard(key)
                tasks.append((key, depot, row["key"], target, chunk))
        groups.append((files, tasks))
        all_tasks.extend(tasks)
    if migrate_legacy:
        with journal_file.open("a", encoding="utf-8") as journal:
            for task in all_tasks:
                if task[0] in completed:
                    journal.write(json.dumps(dict(key=task[0], bytes=task[4]["cb_orig"])) + "\n")
    total = sum(t[4]["cb_orig"] for t in all_tasks)
    done_bytes = sum(t[4]["cb_orig"] for t in all_tasks if t[0] in completed)
    resumed = done_bytes
    started = time.monotonic()

    state_file.parent.mkdir(parents=True, exist_ok=True)
    save_state(state_file, dict(identity=identity, total=total, name=plan["name"]))
    journal = journal_file.open("a", encoding="utf-8", buffering=1)

    stop_progress = threading.Event()
    previous = [time.monotonic(), transfer_meter.read() if transfer_meter else done_bytes]
    phase = "files"

    def progress():
        now = time.monotonic()
        received = transfer_meter.read() if transfer_meter else done_bytes
        speed = (received - previous[1]) / max(now - previous[0], 0.001)
        previous[:] = [now, received]
        emit("progress", phase=phase, completed=done_bytes, total=total, speed=speed,
             connections=concurrency.limit)

    def report_progress():
        while not stop_progress.wait(1):
            progress()

    def work(task):
        key, depot, depot_key, target, chunk = task
        body, _ = core.fetch_chunk(depot, chunk["sha"], None, depot_key)
        # Verify each fetched chunk before writing.
        if len(body) != chunk["cb_orig"] or hashlib.sha1(body).hexdigest() != chunk["sha"]:
            raise RuntimeError("下载数据不完整：" + target.name)
        return task, body

    progress()
    reporter = threading.Thread(target=report_progress, daemon=True)
    reporter.start()
    with futures.ThreadPoolExecutor(max_workers=concurrency.maximum) as executor:
        active = set()
        try:
            # Preserve depot layering: language/shared files can replace a base file.
            for files, tasks in groups:
                phase = "files"
                emit("stage", phase=phase, message="正在准备游戏目录与文件空间，尚未开始传输…")
                remaining = Counter(task[3] for task in tasks if task[0] not in completed)
                sizes = {target: file["size"] for target, file in files}
                opened_targets, prepared_directories = set(), set()
                phase = "transfer"
                emit("stage", phase=phase, message="正在下载游戏文件…")
                pending_tasks = iter(t for t in tasks if t[0] not in completed)
                for _ in range(concurrency.limit):
                    task = next(pending_tasks, None)
                    if task is None:
                        break
                    active.add(executor.submit(work, task))
                while active:
                    ready, active = futures.wait(active, timeout=0.25, return_when=futures.FIRST_COMPLETED)
                    for future in ready:
                        task, body = future.result()
                        key, _, _, target, chunk = task
                        if target.parent not in prepared_directories:
                            target.parent.mkdir(parents=True, exist_ok=True)
                            prepared_directories.add(target.parent)
                        mode = "r+b" if target in opened_targets or target.exists() else "wb"
                        remaining[target] -= 1
                        with target.open(mode) as output:
                            output.seek(chunk["offset"])
                            output.write(body)
                            if remaining[target] == 0:
                                output.truncate(sizes[target])
                        opened_targets.add(target)
                        journal.write(json.dumps(dict(key=key, bytes=len(body))) + "\n")
                        journal.flush()
                        completed.add(key)
                        done_bytes += len(body)
                    received = transfer_meter.read() if transfer_meter else done_bytes-resumed
                    concurrency.observe(received)
                    while len(active) < concurrency.limit:
                        following = next(pending_tasks, None)
                        if following is not None:
                            active.add(executor.submit(work, following))
                        else:
                            break
                # Empty files/directories need no network transfer.
                for target, file in files:
                    if file["flags"] & 64:
                        target.mkdir(parents=True, exist_ok=True)
                    elif not file["chunks"] and not target.exists():
                        target.parent.mkdir(parents=True, exist_ok=True)
                        with target.open("wb") as output:
                            output.truncate(file["size"])
        finally:
            stop_progress.set()
            reporter.join()
            for future in active:
                future.cancel()
            journal.close()
    progress()


def register_library(steam, library, appid, size):
    target = steam / "steamapps" / "libraryfolders.vdf"
    if target.exists():
        tree, _ = read_vdf(target.read_text(encoding="utf-8"))
    else:
        tree = {"libraryfolders": {"0": {"path": str(steam), "apps": {}}}}
    folders = tree["libraryfolders"]
    selected = next((item for item in folders.values() if isinstance(item, dict)
                     and os.path.normcase(os.path.abspath(item.get("path", ""))) == os.path.normcase(os.path.abspath(library))), None)
    if selected is None:
        index = str(max((int(key) for key in folders if key.isdigit()), default=-1) + 1)
        selected = folders[index] = dict(path=str(library), label="", contentid=str(time.time_ns()),
                                         totalsize="0", update_clean_bytes_tally="0", time_last_update_verified="0", apps={})
    selected.setdefault("apps", {})[str(appid)] = str(size)
    temporary = target.with_suffix(".vdf.bear-tmp")
    temporary.write_text(vdf_body(tree, 0) + "\n", encoding="utf-8")
    os.replace(temporary, target)


def register_install(job, plan):
    emit("stage", phase="register", message="正在写入 Steam 安装记录…", committing=True)
    steam = Path(job["steam"])
    steamapps = Path(job["library"]) / "steamapps"
    steamapps.mkdir(parents=True, exist_ok=True)
    depotcache = steam / "depotcache"
    depotcache.mkdir(parents=True, exist_ok=True)
    installed = {}
    for depot, row in plan["depots"].items():
        (depotcache / ("%d_%d.manifest" % (depot, row["gid"]))).write_bytes(row["blob"])
        installed[str(depot)] = dict(manifest=str(row["gid"]), size=str(row["manifest"]["total_size"]))
    merge_keys(steam / "config" / "config.vdf", {d: row["key"] for d, row in plan["depots"].items()})
    if plan.get("dlc_only"):
        emit("complete", message="所选 DLC 文件下载完成，已保留本体的 Steam 安装记录。",
             directory=str(plan["destination"]))
        return
    size = str(sum(row["manifest"]["total_size"] for row in plan["depots"].values()))
    app = dict(appid=str(plan["appid"]), universe="1", LauncherPath=str(steam / "steam.exe"),
               name=plan["name"], StateFlags="4", installdir=plan["installdir"],
               LastUpdated=str(int(time.time())), SizeOnDisk=size, buildid=str(plan["buildid"]),
               LastOwner="0", BytesToDownload=size, BytesDownloaded=size, BytesToStage=size,
               BytesStaged=size, UpdateResult="0", AutoUpdateBehavior="1", InstalledDepots=installed,
               UserConfig=dict(language=plan["language"]), MountedConfig=dict(language=plan["language"]))
    target = steamapps / ("appmanifest_%d.acf" % plan["appid"])
    temporary = target.with_suffix(".acf.bear-tmp")
    temporary.write_text(vdf_body({"AppState": app}, 0) + "\n", encoding="utf-8")
    os.replace(temporary, target)
    register_library(steam, Path(job["library"]), plan["appid"], size)
    emit("complete", message="下载完成，已写入 Steam 安装记录。重新打开 Steam 后查看。",
         directory=str(plan["destination"]))


def cached_plan(job):
    file = Path(job["state"]).with_name("plan.json")
    if job.get("resume", True) and file.exists():
        saved = json.loads(file.read_text(encoding="utf-8"))
        if (saved["library"] == job["library"] and saved["requested_language"] == job["language"]
                and saved["appid"] == job["appid"] and download_selection(saved) == download_selection(job)):
            emit("stage", phase="manifest", message="正在恢复上次的下载任务…")
            result = {k: saved[k] for k in ("appid", "name", "installdir", "buildid", "language")}
            result["destination"] = Path(saved["destination"])
            result.update(download_selection(saved))
            result["depots"] = {}
            for depot, row in saved["depots"].items():
                blob = Path(row["manifest_file"]).read_bytes()
                result["depots"][int(depot)] = dict(gid=row["gid"], key=row["key"], blob=blob,
                                                    manifest=core.parse_manifest(blob, row["key"]), dlcappid=row.get("dlcappid", 0))
            return result
    plan = resolve_plan(job)
    saved = {k: plan[k] for k in ("appid", "name", "installdir", "buildid", "language")}
    saved.update(destination=str(plan["destination"]), library=job["library"], requested_language=job["language"],
                 **download_selection(job),
                 depots={str(depot): dict(gid=row["gid"], key=row["key"], dlcappid=row.get("dlcappid", 0),
                                          manifest_file=core.manifest_cache_path(depot, row["gid"]))
                         for depot, row in plan["depots"].items()})
    file.parent.mkdir(parents=True, exist_ok=True)
    file.write_text(json.dumps(saved), encoding="utf-8")
    return plan


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--job")
    parser.add_argument("--self-test")
    parser.add_argument("--plan-only", action="store_true")
    parser.add_argument("--client-status", action="store_true")
    parser.add_argument("--status-file")
    args = parser.parse_args()
    if args.client_status:
        import client_status
        return client_status.run(args.status_file)
    if args.self_test:
        import download_tests
        return download_tests.run(args.self_test)
    job = json.loads(Path(args.job).read_text(encoding="utf-8-sig"))
    os.environ["BEAR_DOWNLOAD_CACHE"] = job["cache"]
    configure_network(os.environ.get("BEAR_DOWNLOAD_PROXY", ""), job.get('network_routes', ''), job.get('local_sni', True), job.get('download_proxy', 'direct'))
    try:
        plan = cached_plan(job)
        emit("plan", name=plan["name"], directory=str(plan["destination"]), depots=len(plan["depots"]))
        if args.plan_only:
            return 0
        configure_content(job)
        download_plan(plan, Path(job["state"]), job["jobs"])
        register_install(job, plan)
        return 0
    except Exception as error:
        emit("error", message="下载连接暂时不可用，请检查网络后继续。" if isinstance(error, (requests.RequestException, urllib.error.URLError)) or re.search(r"https?://", str(error)) else str(error))
        return 1
    finally:
        core.save_code_cache()



if __name__ == "__main__":
    sys.exit(main())
