# Third-party notices

## pyatv

The Apple TV protocol handling in this plugin (Companion link, HAP pairing, MRP over AirPlay, OPACK) is a .NET port
based on the open-source Python library pyatv: https://github.com/postlund/pyatv

    MIT License

    Copyright (c) 2017-2025 Pierre Ståhl

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

Before release, check the copyright line against pyatv's current LICENSE file.

## srptools (reference only)

The SRP byte encoding in `Protocol/Hap/SrpClient.cs` was matched against the Python library srptools
(BSD-3-Clause, https://github.com/idlesign/srptools), which pyatv uses. No srptools code is included.

## BouncyCastle.Cryptography

Ed25519, X25519 and the RFC 5054 SRP group come from the NuGet package BouncyCastle.Cryptography (MIT,
https://www.bouncycastle.org/csharp/licence.html). Its DLL ships with the plugin; include its license in the
release zip.
