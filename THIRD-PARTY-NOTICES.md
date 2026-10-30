# Third-party notices

RynthCore ships the following third-party material inside its own files.

## Phosphor Icons

- What: the Phosphor icon font (`Phosphor.ttf`, version 2.1, regular weight), used for the icons in the ImGui panels.
- Where: `src/RynthCore.Engine/Assets/Fonts/Phosphor.ttf`, embedded in `RynthCore.Engine.dll` as a resource.
- Source: https://phosphoricons.com (Tobias Fried and Helena Zhang)
- Licence: MIT, reproduced below and in `src/RynthCore.Engine/Assets/Fonts/Phosphor.LICENSE`.

```
MIT License

Copyright (c) 2020-2021 Phosphor Icons

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

## Barlow Semi Condensed

- What: the Barlow Semi Condensed font, SemiBold and Bold (`BarlowSemiCondensed-SemiBold.ttf`, `BarlowSemiCondensed-Bold.ttf`), the narrow label face of the instrument dashboard (plugin windows get it through GetUiFont, Narrow*).
- Where: `src/RynthCore.Engine/Assets/Fonts/`, embedded in `RynthCore.Engine.dll` as resources.
- Source: https://github.com/google/fonts/tree/main/ofl/barlowsemicondensed (The Barlow Project Authors, https://github.com/jpt/barlow)
- Licence: SIL Open Font License 1.1, in full in `src/RynthCore.Engine/Assets/Fonts/Barlow.LICENSE`. The fonts are bundled unmodified; the licence allows bundling them with software, not selling them alone.

```
Copyright 2017 The Barlow Project Authors (https://github.com/jpt/barlow)

This Font Software is licensed under the SIL Open Font License, Version 1.1.
```

## Chorizite DatReaderWriter (format reference for the dat readers)

- What: the layout of Asheron's Call .dat files (header, block chains, the directory B-tree) and of the Texture and Palette files, used as the reference when RynthCore's own dat reader and icon decoder were written (2026-10-05). The code is RynthCore's own (MIT); this notice is kept because the library was the format reference.
- Where: `src/RynthCore.StatusAgent/Dat/DatDatabase.cs` and `Dat/IconDecoder.cs`, and `src/RynthCore.Engine/UI/ScriptWindows/AcIconDecoder.cs`. The notice is also in `DatDatabase.cs`'s header. The DXT decoder (`Dat/DxtUtil.cs`) was written from the public S3TC block-compression format.
- Source: https://github.com/Chorizite/DatReaderWriter (ACClientLib)
- Licence: MIT, reproduced below.

```
MIT License

Copyright 2024 ACClientLib

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```
