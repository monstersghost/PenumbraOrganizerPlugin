# Penumbra Organizer

Penumbra Organizer is an in-game Dalamud plugin for FINAL FANTASY XIV that helps you organize your
installed [Penumbra](https://github.com/xivdev/Penumbra) mods without manually editing Penumbra's
files.

It communicates with Penumbra through its IPC interface and lets you preview proposed changes
before applying them.

## Features

* Organize mods by creator, mod type, or a combination of both
* Review every proposed folder change before applying it
* Protect individual mods from sorting and restoration operations
* Protect entire folders, including all mods contained in their subfolders
* Automatically protect mods managed by [Heliosphere](https://heliosphere.app/)
* Create multiple rollback snapshots and restore any previously saved library state
* Remove empty folders left behind after mods are reorganized
* Export your mod organization to an Excel workbook
* Import edited organization workbooks back into the plugin

## Installation

Penumbra Organizer is currently distributed through a custom Dalamud plugin repository.

1. Open Dalamud settings using `/xlsettings`.
2. Select the Experimental tab.
3. Add the repository URL shown below to the custom plugin repositories list.
4. Click the + button.
5. Click Save and Close.
6. Open the Dalamud Plugin Installer.
7. Find Penumbra Organizer under Available Plugins and install it.

Repository URL:

```text
https://raw.githubusercontent.com/monstersghost/PenumbraOrganizerPlugin/main/repo.json
```

**Important:** Do not install the plugin by downloading the release archive and extracting it into
the `devPlugins` folder. Manual installations do not receive normal update notifications and must
be updated by hand. They may also conflict with the custom-repository version. Remove any manually
installed copy before installing Penumbra Organizer through the repository.

Once it's installed, see [docs/USER_GUIDE.md](docs/USER_GUIDE.md) for how to use each tab.

## Support

Found a bug, hit unexpected behavior, or have a feature idea? Join our
[Discord](https://discord.gg/MhQzVJ65c) or open a GitHub issue.

## Contributing

Contributions are welcome, but please open an issue before beginning implementation. Discussing
the change first helps confirm that it fits the project's scope and may reveal a better approach
that benefits more users.

For build instructions and contributor documentation, see
[docs/DEVELOPMENT.md](docs/DEVELOPMENT.md).
