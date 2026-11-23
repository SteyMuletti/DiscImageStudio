# License notice

This project is a modified, stripped-down fork of
[Disc Image Studio](https://github.com/JiaFeiMiao-K-Cat/DiscImageStudio).
Disc Image Studio is licensed under the **GNU General Public License v3.0 or later
(GPL-3.0-or-later)**; the full text is in [LICENSE](LICENSE). This fork is distributed under the
same license and comes with no warranty.

Copyright (c) 2026 Disc Image Studio contributors.

The CD interleave implementation contains code ported and modified from
[CDImage](https://github.com/arduinocelentano/cdimage), Copyright (C) 2008-2022
arduinocelentano, licensed GPL-3.0-or-later.

Modifications in this fork: removed the DVD, Blu-ray, desktop app, direct-burning, calibration
UI, tests and documentation translations; added the `cd-mixtape` command (music tracks plus
disc art), a Verbatim AZO disc option, conversion and one-step scripts, and the MixtapeCD desktop
app (src/MixtapeCd.App).

If you distribute binaries of this fork, the GPL requires you to provide the matching complete
source code. The desktop app uses [Avalonia](https://github.com/AvaloniaUI/Avalonia) (MIT). The Windows app
download bundles [FFmpeg](https://ffmpeg.org/) (GPL build; license included as FFMPEG-LICENSE.txt).
Third-party components keep their own licenses; see
[THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt).
