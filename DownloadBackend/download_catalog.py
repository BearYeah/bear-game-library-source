import copy
import re
"""Windows/public depot selection shared by Bear and its metadata cache server."""

def select_depots(info, language, dlc_ids=(), dlc_only=False):
    selected = {str(appid) for appid in dlc_ids}
    choices = []
    for number, item in info.get("depots", {}).items():
        if not number.isdigit():
            continue
        owner = str(item.get("dlcappid") or "")
        if (owner and owner not in selected) or (not owner and dlc_only):
            continue
        config = item.get("config") or {}
        platforms = config.get("oslist", "").split(",")
        if platforms != [""] and "windows" not in platforms:
            continue
        public = (item.get("manifests") or {}).get("public") or {}
        if public.get("gid"):
            choices.append((int(number), item, public, owner))
    languages = {}
    for _, item, _, owner in choices:
        languages.setdefault(owner, set()).add(item.get("config", {}).get("language"))
    return {number: (item, public) for number, item, public, owner in choices
            if item.get("config", {}).get("language") in
            (None, "", language if language in languages[owner] else "english")}


def merge_dlc_depots(info, dlc_info, dlc_appid, parent_appid):
    """Use the selected DLC's public metadata to identify its own depots."""
    target = info.setdefault("depots", {})
    for depot, item in dlc_info.get("depots", {}).items():
        if not depot.isdigit() or str(item.get("depotfromapp", "")) == str(parent_appid):
            continue
        row = target.setdefault(depot, copy.deepcopy(item))
        row.setdefault("dlcappid", str(dlc_appid))
        if not (row.get("manifests") or {}).get("public") and (item.get("manifests") or {}).get("public"):
            row.setdefault("manifests", {})["public"] = copy.deepcopy(item["manifests"]["public"])


def supplement_depots(info, catalog):
    """补充平台与 DLC 归属；版本 gid 始终使用 Steam 元数据。"""
    dlc_apps = {str(d["appid"]) for d in catalog.get("dlc", []) if d.get("has_depots")}
    dlc_map = catalog.get("depot_dlc_map", {})
    platforms = catalog.get("depot_oslist_map", {})
    for depot, item in info.get("depots", {}).items():
        if not depot.isdigit():
            continue
        owner = str(dlc_map.get(depot, ""))
        if owner and (owner != depot or owner in dlc_apps):
            item.setdefault("dlcappid", owner)
        if platforms.get(depot):
            item.setdefault("config", {}).setdefault("oslist", platforms[depot].lower())



def parse_lua(txt):
    """addappid(depot[,0,"key"] / setManifestid(depot,"gid") / addappid(appid) 本体。"""
    keys, gids, base = {}, {}, None
    for m in re.finditer(r'addappid\(\s*(\d+)\s*(?:,\s*\d+\s*(?:,\s*"([0-9a-fA-F]{64})"\s*)?)?\s*\)', txt):
        did, k = int(m.group(1)), m.group(2)
        if k:
            keys[did] = k.lower()
        else:
            base = did
    for m in re.finditer(r'setManifestid\(\s*(\d+)\s*,\s*"(\d+)"', txt):
        gids[int(m.group(1))] = int(m.group(2))
    return {"base": base, "keys": keys, "gids": gids}
