# Sarasa Gothic SC

The application bundles the original hinted TrueType fonts from Sarasa Gothic
v1.0.41 (Simplified Chinese):

- `SarasaGothicSC-Regular.ttf`: normal text, weight 400.
- `SarasaGothicSC-Bold.ttf`: headings, weight 700.
- `SarasaGothicSC-Startup.ttf`: derived subset for the native loading label, 111,592 bytes. It preserves the source font metrics and family name, with ASCII and loading/error-label characters. Other native error characters use Windows font fallback.
- `SarasaGothicSC-UI-Regular.ttf`: UI subset, 352,316 bytes, 685 code points.
- `SarasaGothicSC-UI-Bold.ttf`: UI subset, 351,596 bytes, the same code points.

Source: https://github.com/be5invis/Sarasa-Gothic/releases/tag/v1.0.41

Archive: `SarasaGothicSC-TTF-1.0.41.7z`

SHA-256:

```text
c8c5209fbd6faf572634f4d4ef70af28ccfe2f7f18d8fdb564a8b114743af740  SarasaGothicSC-TTF-1.0.41.7z
de4fd9f7f176e5745288d9eca1bf5a954606f43a24fa9f6dc210bb8ff3b527f8  SarasaGothicSC-Regular.ttf
25e68338fb4bcf79a1292fd4236446fdfc5eee20dadd3eee695556ee8adf7548  SarasaGothicSC-Bold.ttf
c061f292c02f4d3dc72ec4b37c5f2f44dd513c454b8cdb7f3414d0983d17ee81  SarasaGothicSC-Startup.ttf
bddc4d8e63b9b33ce41fd6db67b2a9ed722619fae80a67f35d872f7497d8f6ce  SarasaGothicSC-UI-Regular.ttf
6f190ef5f8750e8389d77f2e67ecbe91b316ca3e07cec631a931b3ce4cd126ba  SarasaGothicSC-UI-Bold.ttf
```

Copyright and SIL Open Font License 1.1: [OFL.txt](OFL.txt).

`../fonts.css` uses `unicode-range` to load the UI subsets first for characters
collected from application source and bundled presets. Characters outside these
ranges lazily load the original full fonts, preserving CJK coverage and metrics.
All OpenType layout features, including tabular numerals, are retained.
The Windows startup label loads the small startup subset privately. The project
copies this directory, including the license, to build and publish output.
System font installation and network access are unnecessary at runtime.

Regenerate all subsets and `../fonts.css` with `python scripts/Build-StartupFont.py`
using fonttools after adding labels. The derived subsets are distributed under the
same SIL Open Font License 1.1.
