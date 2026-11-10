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

## Microsoft WebView2 SDK (the in-game web panel)

- What: `Microsoft.Web.WebView2.Core.dll` (the managed WebView2 Core wrapper) and the x86 `WebView2Loader.dll`, which the engine uses to host the in-game web panel on the CoreCLR build. They find and drive the Evergreen WebView2 Runtime already installed on the machine; the runtime itself is not shipped.
- Where: shipped in `Runtime\` of every CoreCLR install, referenced from `src/RynthCore.Engine/RynthCore.Engine.csproj` (NuGet `Microsoft.Web.WebView2` 1.0.2903.40). The package's WinForms and WPF assemblies are not used or shipped.
- Source: https://www.nuget.org/packages/Microsoft.Web.WebView2
- Licence: Microsoft's BSD-style licence, reproduced below from the package's own LICENSE.txt.

```
Copyright (C) Microsoft Corporation. All rights reserved.

Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions are
met:

   * Redistributions of source code must retain the above copyright
notice, this list of conditions and the following disclaimer.
   * Redistributions in binary form must reproduce the above
copyright notice, this list of conditions and the following disclaimer
in the documentation and/or other materials provided with the
distribution.
   * The name of Microsoft Corporation, or the names of its contributors 
may not be used to endorse or promote products derived from this
software without specific prior written permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS
"AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT
LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR
A PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT
OWNER OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL,
SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT
LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE,
DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY
THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
(INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
```
