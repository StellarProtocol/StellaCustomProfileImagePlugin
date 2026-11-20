# StellarCustomProfileImagePlugin

A [Stellar Framework](https://github.com/) plugin that lets you upload a custom PNG image as your in-game avatar or namecard, bypassing the in-game camera requirement.

## Features

- **Custom avatar** — upload any PNG as your head portrait (recommended 300×300 px square)
- **Custom namecard** — upload any PNG as your half-body namecard (recommended 468×774 px portrait)
- **In-window preview** — shows the selected image, its size, and whether its shape fits an avatar (square) or a namecard (468×774)
- **Step guide** — the window shows which step you are on, from choosing the image to pressing Upload
- **File picker** — native Windows file dialog filtered to PNG files

## Requirements

- [Stellar Framework](https://github.com/) 2.33.0 or newer installed in the game directory

## Installation

Copy `Stellar.CustomProfleImage.dll` into:

```
<GameDir>\stellar\plugins\customprofleimage\
```

The plugin loads automatically when the game starts.

## Usage

1. Open the Stellar launcher overlay and click **Custom Profile Image**
2. Click **Choose…** and select a PNG (the same image is used for the avatar and the namecard)
3. Click **Apply** — the status shows **Ready**
4. Use an Avatar/Namecard ticket from your inventory or the guild change device, and take the shot without adjusting anything — the game preview shows your image
5. Press **Upload** once

The status line updates as the upload progresses and ends on **Upload complete**. Click **Cancel** before uploading to restore the original game behaviour.

## License

Copyright (C) 2026 speedxpz

Licensed under the [GNU Affero General Public License v3.0](LICENSE).
