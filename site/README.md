# hotsonos.com

Static landing page for HotSonos (no framework). Served by nginx on docker1 from `~/clover-dev/sites/hotsonos`. Nothing in GitHub Actions copies this folder, so merging to `master` does not deploy the site. Copy the built files onto that host after `python3 build.py`.

- `build.py` — page content (index, privacy, 404, robots, sitemap, manifest). Run `python3 build.py` after edits.
- `assets/logo.png` — the one logo file. Swap it, then run `python3 make-icons.py` to regenerate favicons and `assets/og-image.png`.
- `assets/site.css` — styles.
- `deploy/hotsonos.conf` — nginx server block (copy to `~/clover-dev/conf.d/`).
