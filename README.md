# StellarCustomProfileImagePlugin

A [Stellar Framework](https://github.com/) plugin that lets you upload a custom PNG image as your in-game avatar or namecard, bypassing the in-game camera requirement.

## Features

- **Custom avatar** — upload any PNG as your head portrait (recommended 300×300 px square)
- **Custom namecard** — upload any PNG as your half-body namecard (recommended 468×774 px portrait)
- **In-window preview** — shows the selected image scaled to the window before uploading
- **File picker** — native Windows file dialog filtered to PNG files

## Requirements

- [Stellar Framework](https://github.com/) installed in the game directory

## Installation

Copy `Stellar.CustomProfleImage.dll` into:

```
<GameDir>\stellar\plugins\customprofleimage\
```

The plugin loads automatically when the game starts.

## Usage

1. Open the Stellar launcher overlay and click **Custom Profile Image**
2. Click **Choose File…** and select a PNG
3. Click **Make Avatar** or **Make Namecard**
4. Open your inventory, use the Avatar Change or Namecard Change card, and take any shot — the plugin intercepts the upload and replaces the image with your chosen file

The status line updates as the upload progresses. Click **Cancel** at any time to restore the original game behaviour.

## License

Copyright (C) 2026 speedxpz

Licensed under the [GNU Affero General Public License v3.0](LICENSE).
