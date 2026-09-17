# Sarasa Gothic SC

The application bundles the original hinted TrueType fonts from Sarasa Gothic
v1.0.41 (Simplified Chinese):

- `SarasaGothicSC-Regular.ttf`: normal text, weight 400.
- `SarasaGothicSC-Bold.ttf`: headings, weight 700.

Source: https://github.com/be5invis/Sarasa-Gothic/releases/tag/v1.0.41

Archive: `SarasaGothicSC-TTF-1.0.41.7z`

SHA-256:

```text
c8c5209fbd6faf572634f4d4ef70af28ccfe2f7f18d8fdb564a8b114743af740  SarasaGothicSC-TTF-1.0.41.7z
de4fd9f7f176e5745288d9eca1bf5a954606f43a24fa9f6dc210bb8ff3b527f8  SarasaGothicSC-Regular.ttf
25e68338fb4bcf79a1292fd4236446fdfc5eee20dadd3eee695556ee8adf7548  SarasaGothicSC-Bold.ttf
```

Copyright and SIL Open Font License 1.1: [OFL.txt](OFL.txt).

`../fonts.css` loads these files directly for the interface, monitor, and send
editor. The Windows startup label loads the regular face privately. The project
copies this directory, including the license, to build and publish output.
System font installation and network access are unnecessary at runtime.
