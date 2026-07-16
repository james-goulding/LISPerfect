# LISPerfect

A modern Common Lisp editor for Windows, built as an alternative to Emacs/Slime
for people who want a clean IDE experience.

## Features

- Multi-file tabs with Common Lisp syntax highlighting
- Persistent SBCL REPL with error recovery
- Send-expression (Ctrl+Enter) and Run File (F5)
- Autocomplete via live SBCL introspection (Ctrl+Space)
- Jump to definition (F12)
- Project folder tree pane
- Bundled Quicklisp integration
- Paredit-lite (optional auto-close of parens and quotes)
- Dark and light themes
- Persistent settings (fonts, window position, splitter position)
- Auto-save (optional)
- Recent files, REPL history

## Installation

Download the latest installer from [Releases](../../releases) and run it.
LISPerfect bundles SBCL, so nothing else is required.

## Building from source

Requires:

- Visual Studio 2022 with the ".NET desktop development" workload
- .NET 8 SDK
- SBCL on your PATH (for development)

Open `LISPerfect.sln` in Visual Studio, hit F5. Uses AvalonEdit (installed via NuGet).

## License

MIT. See LICENSE.
