# Bundled rendering assets

These files are compiled into `Spatial.Rendering.Skia` as `EmbeddedResource`
and are the renderer's **only** source of fonts and sprite images. They are
pinned so golden images are reproducible; the host's system fonts and any
remote URL are deliberately not consulted (ADR-0049).

## Fonts

| File | Face | Licence | SHA-256 |
| --- | --- | --- | --- |
| `Fonts/NotoSans-Regular.ttf` | Noto Sans Regular 2.003 | SIL Open Font License 1.1 (`Fonts/OFL.txt`) | `dac8e68fe43fca59d522fa5f763322cfb4a919c28957656c58e7836d915307d0` |
| `Fonts/NotoSans-Bold.ttf` | Noto Sans Bold 2.003 | as above | `d62ff7c27ae901ae9b5c3ef0cae4a1f091e28a7ce62b9e0fed86a38185f84971` |
| `Fonts/NotoSans-Italic.ttf` | Noto Sans Italic 2.003 | as above | `fd0142325f12c857b3443c2390f7f4a697b5c4017fbad4cfd16218cf33c4ddba` |
| `Fonts/NotoSans-BoldItalic.ttf` | Noto Sans Bold Italic 2.003 | as above | `4a44bc45d89669b612d00483f01233b43caaef695f1998702126154a37093180` |

Source: `https://github.com/notofonts/noto-fonts` tag `v20201206-phase3`,
path `hinted/ttf/NotoSans/NotoSans-<weight>[-Italic].ttf`. All four faces are
the same upstream tag, so the family is one version.

`FontFaceRegistry` holds the faces and resolves a `text-font` request against
them: a name's family and weight/style if bundled, else the nearest bundled
weight (heavier first, then lighter) in the requested or upright style, else the
next name in the list, else the default face. A family the bundle does not
carry is a substitution, not an error.

Changing a font (or its bytes) requires updating this table, the ADR and the
golden images; the registry hashes each stream it reads against the face's pin,
so a silent swap fails a test.

## Sprites

| File | Name (`icon-image`) |
| --- | --- |
| `Sprites/default-marker.svg` | `default-marker` |
| `Sprites/marker-circle.svg` | `marker-circle` |
| `Sprites/marker-diamond.svg` | `marker-diamond` |
| `Sprites/marker-ring.svg` | `marker-ring` |
| `Sprites/marker-square.svg` | `marker-square` |
| `Sprites/marker-triangle.svg` | `marker-triangle` |

Sprite names are the file name without the extension. A style may only
reference a bundled name (ADR-0049); remote sprite URLs are rejected. The set
is the marker shapes, all on the same 24x24 template.
