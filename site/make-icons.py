#!/usr/bin/env python3
"""Regenerate favicons, app icons, and the OG image from assets/logo.png (the single logo source).
Usage: python3 make-icons.py   (requires Pillow)"""
import pathlib
from PIL import Image, ImageDraw, ImageFont
here=pathlib.Path(__file__).parent
logo=Image.open(here/"assets/logo.png").convert("RGBA")
for name,size in [("apple-touch-icon.png",180),("icon-192.png",192),("icon-512.png",512),("favicon-32.png",32),("favicon-16.png",16)]:
    logo.resize((size,size),Image.LANCZOS).save(here/name,optimize=True)
logo.save(here/"favicon.ico",sizes=[(16,16),(32,32),(48,48),(64,64)])
og=Image.new("RGB",(1200,630),(24,26,32)); d=ImageDraw.Draw(og)
for x in range(1200):
    t=x/1199; d.line([(x,622),(x,630)],fill=(int(255-25*t),int(122-92*t),int(24+16*t)))
L=logo.resize((300,300),Image.LANCZOS); og.paste(L,(110,165),L)
def f(sz,bold):
    try: return ImageFont.truetype("/usr/share/fonts/truetype/dejavu/DejaVuSans%s.ttf"%("-Bold" if bold else ""),sz)
    except OSError: return ImageFont.load_default()
d.text((470,215),"HotSonos",font=f(96,True),fill=(245,245,247))
d.text((474,335),"Global hotkeys for your Sonos system.",font=f(34,False),fill=(200,200,206))
d.text((474,385),"Windows tray app · local network only · no cloud",font=f(28,False),fill=(255,140,60))
og.save(here/"assets/og-image.png",optimize=True)
print("icons regenerated")
