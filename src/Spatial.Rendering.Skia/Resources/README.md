# Bundled rendering assets

These files are compiled into `Spatial.Rendering.Skia` as `EmbeddedResource`
and are the renderer's **only** source of fonts and sprite images. They are
pinned so golden images are reproducible; the host's system fonts and any
remote URL are deliberately not consulted (ADR-0049).

## Fonts

| File | Version | Licence | SHA-256 |
| --- | --- | --- | --- |
| `Fonts/NotoSans-Regular.ttf` | Noto Sans Regular 2.003 | SIL Open Font License 1.1 (`Fonts/OFL.txt`) | `dac8e68fe43fca59d522fa5f763322cfb4a919c28957656c58e7836d915307d0` |

Source: `https://github.com/notofonts/noto-fonts` tag `v20201206-phase3`,
path `hinted/ttf/NotoSans/NotoSans-Regular.ttf`.

Changing the font (or its bytes) requires updating this table, the ADR and the
golden images; the `BundledFont` loader hashes the stream it reads so a silent
swap fails a test.

## Sprites

| File | Name (`icon-image`) |
| --- | --- |
| `Sprites/default-marker.svg` | `default-marker` |

Sprite names are the file name without the extension. A style may only
reference a bundled name (ADR-0049); remote sprite URLs are rejected.
