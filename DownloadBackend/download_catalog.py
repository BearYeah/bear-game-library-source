import re
"""Windows/public depot selection shared by Bear and its metadata cache server."""

def select_depots(info, language):
    choices = []
    for number, item in info.get("depots", {}).items():
        if not number.isdigit() or item.get("dlcappid"):
            continue
        config = item.get("config") or {}
        platforms = config.get("oslist", "").split(",")
        if platforms != [""] and "windows" not in platforms:
            continue
        public = (item.get("manifests") or {}).get("public") or {}
        if public.get("gid"):
            choices.append((int(number), item, public))
    languages = {row[1].get("config", {}).get("language") for row in choices}
    chosen_language = language if language in languages else "english"
    return {number: (item, public) for number, item, public in choices
            if item.get("config", {}).get("language") in (None, "", chosen_language)}


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
