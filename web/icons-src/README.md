# App icon sources (#142)

`web/public/` holds the *generated* PWA icons. This directory holds what they are
generated **from**, so they can be reproduced rather than being opaque binaries.

- `../public/favicon.svg` — the standard mark. Full-bleed rounded square; used
  as-is for the browser tab and for the non-maskable PWA icons.
- `maskable.svg` — the **maskable** variant. Android crops a maskable icon to a
  circle/squircle and only the middle ~80% is guaranteed visible, so the mark is
  scaled to ~62% on a full-bleed aubergine field. Using the standard art here
  would clip the egg.

This directory is deliberately outside `public/`, so the sources are not copied
into `dist/` or precached by the service worker.

## Regenerate

```sh
cd web
rsvg-convert -w 192 -h 192 public/favicon.svg   -o public/icon-192.png
rsvg-convert -w 512 -h 512 public/favicon.svg   -o public/icon-512.png
rsvg-convert -w 192 -h 192 icons-src/maskable.svg -o public/icon-192-maskable.png
rsvg-convert -w 512 -h 512 icons-src/maskable.svg -o public/icon-512-maskable.png
# iOS: real PNG, and opaque — iOS composites transparency onto black.
rsvg-convert -w 180 -h 180 icons-src/maskable.svg -o public/apple-touch-icon.png
magick public/apple-touch-icon.png -background "#4a154b" -flatten -alpha off public/apple-touch-icon.png
```
