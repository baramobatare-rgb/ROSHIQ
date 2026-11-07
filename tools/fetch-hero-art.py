from pathlib import Path
import concurrent.futures, urllib.request, struct, zipfile
root=Path(__file__).resolve().parents[1]
keys=[line.split('|')[1] for line in (root/'src/Roshiq.Data/Resources/heroes.txt').read_text(encoding='utf-8-sig').splitlines() if line and not line.startswith('#')]
out=root/'src/Roshiq.App/Assets/HeroArt'
out.mkdir(exist_ok=True)
def fetch(key):
    p=out/(key+'.png')
    if not p.exists():
        url='https://cdn.akamai.steamstatic.com/apps/dota2/images/dota_react/heroes/'+key+'.png'
        with urllib.request.urlopen(url, timeout=30) as response: data=response.read()
        if data[:8]!=b'\x89PNG\r\n\x1a\n': raise ValueError(key)
        p.write_bytes(data)
    data=p.read_bytes()
    w,h=struct.unpack('>II',data[16:24])
    if w<128 or h<72: raise ValueError((key,w,h))
    return key,w,h
with concurrent.futures.ThreadPoolExecutor(max_workers=8) as pool:
    results=list(pool.map(fetch,keys))
with zipfile.ZipFile(root/'src/Roshiq.App/Assets/HeroArt.zip','w',zipfile.ZIP_DEFLATED) as z:
    for key in sorted(keys): z.write(out/(key+'.png'),key+'.png')
print(f'Bundled {len(results)} native portraits; resolutions: {sorted(set((w,h) for _,w,h in results))}')

