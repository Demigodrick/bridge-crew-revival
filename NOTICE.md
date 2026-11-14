# Bridge Crew Revival: licence and notices

Copyright (C) 2026 Demigodrick and the Bridge Crew Revival contributors.

Bridge Crew Revival is free software: you can redistribute it and/or modify it under the terms of the GNU General
Public License as published by the Free Software Foundation, version 3 of the License, or (at your option) any later
version. It is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied
warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See [LICENSE](LICENSE) for the full text.

## Additional permission (GPL-3.0 section 7)

The mod is loaded into Star Trek: Bridge Crew, which is not free software, by BepInEx and Harmony, and calls into
the game's own libraries (Unity, Photon, Wwise, SteamVR and the game's code). As a special exception, the copyright
holders of Bridge Crew Revival give you permission to combine and run this program with Star Trek: Bridge Crew and
its libraries, and with BepInEx and Harmony, and to distribute the result, without the GPL's terms applying to those
parts. You must still follow the GPL for Bridge Crew Revival's own code. If you change this program, you may extend
this exception to your version, but you don't have to; if you don't, delete this paragraph from your version.

## What this licence doesn't cover

These parts keep their own terms, given below or in the file named.

### Ship models (`assets/ships`)

These aren't under the GPL. Don't take them out of the mod to share or use elsewhere without asking their authors.

- **Nova class** (`assets/ships/Nova`): by Raul Mamoru, included with his permission; see its `CREDITS.txt`.
- **Sovereign class** (`assets/ships/Sovereign`): by YannikB98, made for Bridge Crew Revival.
- **Kelvin Enterprise** (`assets/ships/AbramsEnterprise`): by Ricky "MadMan1701A" Wallace
  (www.madshipyard.com); see its `CREDITS.txt`.

Star Trek and related marks are trademarks of CBS Studios / Paramount Global. Bridge Crew Revival is an unofficial
fan project, not affiliated with or endorsed by Ubisoft, Red Storm Entertainment, CBS Studios or Paramount.

### VR walking and Quest controller buttons

Adapted from BamaSuperman's [Bridge Crew VR add-ons](https://github.com/BamaSuperman/bridge-crew-vr-addons),
under the MIT licence:

```
MIT License

Copyright (c) 2026 Bridge Crew companion plugin contributors

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

### RNNoise (`rnnoise.dll`, voice chat's noise suppression)

Built from [RNNoise](https://github.com/xiph/rnnoise) 0.2 (`tools/rnnoise/build.sh`):

```
Copyright (c) 2007-2017, 2024 Jean-Marc Valin
Copyright (c) 2023 Amazon
Copyright (c) 2017, Mozilla
Copyright (c) 2005-2017, Xiph.Org Foundation
Copyright (c) 2003-2004, Mark Borgerding

Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions
are met:

- Redistributions of source code must retain the above copyright
notice, this list of conditions and the following disclaimer.

- Redistributions in binary form must reproduce the above copyright
notice, this list of conditions and the following disclaimer in the
documentation and/or other materials provided with the distribution.

- Neither the name of the Xiph.Org Foundation nor the names of its
contributors may be used to endorse or promote products derived from
this software without specific prior written permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS
``AS IS'' AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT
LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR
A PARTICULAR PURPOSE ARE DISCLAIMED.  IN NO EVENT SHALL THE FOUNDATION
OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL,
SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT
LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE,
DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY
THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
(INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
```

### SpeexDSP (`speexdsp.dll`, voice chat's echo cancellation)

Built from [SpeexDSP](https://gitlab.xiph.org/xiph/speexdsp) 1.2.1 (`tools/speexdsp/build.sh`):

```
Copyright 2002-2008 	Xiph.org Foundation
Copyright 2002-2008 	Jean-Marc Valin
Copyright 2005-2007	Analog Devices Inc.
Copyright 2005-2008	Commonwealth Scientific and Industrial Research
                        Organisation (CSIRO)
Copyright 1993, 2002, 2006 David Rowe
Copyright 2003 		EpicGames
Copyright 1992-1994	Jutta Degener, Carsten Bormann

Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions
are met:

- Redistributions of source code must retain the above copyright
notice, this list of conditions and the following disclaimer.

- Redistributions in binary form must reproduce the above copyright
notice, this list of conditions and the following disclaimer in the
documentation and/or other materials provided with the distribution.

- Neither the name of the Xiph.org Foundation nor the names of its
contributors may be used to endorse or promote products derived from
this software without specific prior written permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS
``AS IS'' AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT
LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR
A PARTICULAR PURPOSE ARE DISCLAIMED.  IN NO EVENT SHALL THE FOUNDATION OR
CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL,
EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT LIMITED TO,
PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE, DATA, OR
PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF
LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING
NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
```

### The bridge designer

- [three.js](https://threejs.org), MIT licence: `tools/bridge-designer/web/vendor/three/LICENSE`.
- The released programs include the [.NET runtime](https://github.com/dotnet/runtime) (MIT licence).

### Developer tools (not in the released mod)

`tools/xref/out` includes [Mono.Cecil](https://github.com/jbevain/cecil) and
[ICSharpCode.Decompiler](https://github.com/icsharpcode/ILSpy), each under the MIT licence; see their projects.

### Not included

The game, BepInEx and Harmony aren't part of this repository or the release zip: you need your own copy of the game,
and the installers download BepInEx from its own releases.
