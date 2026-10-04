# 쯔꾸르 기본 에셋 목록 만들기 -> src/UI/Resources/default_assets.json
# 에셋 보기에서 기본 에셋(RTP·새 프로젝트에 들어 있는 그림/소리)을 숨기는 데 씁니다. 파일 내용은 담지 않고 "경로|크기"만 담습니다.
#   2000/2003, XP, VX, VX Ace: 이 PC에 설치된 RTP 폴더 (언어판이 다르면 이름이 달라 여럿을 함께 넣음)
#   MV, MZ: RTP가 따로 없어, GitHub 공개 저장소에 올라온 프로젝트들의 파일 목록(이름·크기)에서
#           서로 다른 프로젝트 MIN_SOURCES곳 이상에 같은 경로·같은 크기로 있는 파일만 (= 그 편집기 버전의 기본 에셋).
#           크기가 다른 같은 이름(직접 고친 Window.png 등)은 넣지 않습니다.
#
#   python scripts\make_default_assets.py            (gh CLI 로그인 필요)
import json, os, re, subprocess, sys, collections

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OUT = os.path.join(ROOT, "src", "UI", "Resources", "default_assets.json")
MIN_SOURCES = 3
PF86 = os.environ.get("ProgramFiles(x86)", r"C:\Program Files (x86)")
APPDATA = os.environ.get("APPDATA", "")

RTPS = {
    "2k": [os.path.join(PF86, v, "RPG2000", "RTP") for v in ("ASCII", "KADOKAWA", "Enterbrain")]
          + [os.path.join(PF86, v, "RPG2003", "RTP") for v in ("ASCII", "KADOKAWA", "Enterbrain")]
          + [os.path.join(APPDATA, "KADOKAWA", "Common", n) for n in ("RPG Maker 2000 RTP", "RPG Maker 2003 RTP")],
    "xp": [os.path.join(PF86, "Common Files", "Enterbrain", "RGSS", "Standard")],
    "vx": [os.path.join(PF86, "Common Files", "Enterbrain", "RGSS2", "RPGVX")],
    "vxace": [os.path.join(PF86, "Common Files", "Enterbrain", "RGSS3", "RPGVXAce")],
}
QUERIES = ["rpg maker mz game", "rpg maker mv game", "rpgmaker mz", "rpgmaker mv game", "RPG Maker MZ project",
           "RPG Maker MV project", "rpgmakermv", "rpgmakermz", "RPGツクールMZ", "RPGツクールMV ゲーム"]
ENC = {".rpgmvp": ".png", ".png_": ".png", ".rpgmvo": ".ogg", ".ogg_": ".ogg", ".rpgmvm": ".m4a", ".m4a_": ".m4a"}
MEDIA = re.compile(r"\.(png|bmp|xyz|jpg|gif|ogg|mp3|wav|mid|midi|m4a|wma|avi|mpg|webm|mp4|ogv|ttf|otf)$", re.I)


def gh(*args):
    r = subprocess.run(["gh", *args], capture_output=True, text=True, encoding="utf-8")
    return r.stdout if r.returncode == 0 else ""


def rtp_lists():
    out = {}
    for fam, dirs in RTPS.items():
        items = set()
        for d in dirs:
            if not os.path.isdir(d):
                continue
            n = 0
            for base, _, files in os.walk(d):
                for f in files:
                    p = os.path.join(base, f)
                    if not MEDIA.search(f):
                        continue
                    rel = os.path.relpath(p, d).replace("\\", "/").lower()
                    items.add(f"{rel}|{os.path.getsize(p)}")
                    n += 1
            print(f"{fam}: {n} files from {d}")
        out[fam] = sorted(items)
    return out


def github_lists():
    repos = set()
    for q in QUERIES:
        for line in gh("search", "repos", q, "-L", "40", "--json", "fullName,size", "--jq", '.[] | select(.size > 20000) | .fullName').split():
            repos.add(line.strip())
    print(f"github: {len(repos)} candidate repos")
    seen = {"mv": collections.defaultdict(set), "mz": collections.defaultdict(set)}
    for r in sorted(repos):
        raw = gh("api", f"repos/{r}/git/trees/HEAD?recursive=1")
        try:
            tree = json.loads(raw).get("tree") or []
        except ValueError:
            continue
        paths = [t["path"] for t in tree]
        eng = "mz" if any(p.endswith("rmmz_core.js") for p in paths) else "mv" if any(p.endswith("rpg_core.js") for p in paths) else None
        if not eng:
            continue
        for t in tree:
            if t["type"] != "blob":
                continue
            m = re.search(r"(?:^|/)((?:img|audio|movies)/.+)$", t["path"])
            if not m:
                continue
            p, s = m.group(1), t["size"]
            root, ext = os.path.splitext(p)
            if ext.lower() in ENC:
                p, s = root + ENC[ext.lower()], s - 16
            seen[eng][(p.lower(), s)].add(r)
    return {e: sorted(f"{p}|{s}" for (p, s), src in v.items() if len(src) >= MIN_SOURCES) for e, v in seen.items()}


def main():
    data = {"note": "RPG Maker default assets (path|size only, no content). Made by scripts/make_default_assets.py"}
    data.update(rtp_lists())
    data.update(github_lists())
    for k, v in data.items():
        if isinstance(v, list):
            print(f"{k}: {len(v)} entries")
    with open(OUT, "w", encoding="utf-8", newline="\n") as f:
        json.dump(data, f, ensure_ascii=False, separators=(",", ":"))
    print("->", OUT, os.path.getsize(OUT), "bytes")


if __name__ == "__main__":
    sys.exit(main())
