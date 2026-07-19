-- Alfred / "Reveal in Finder": show a file selected in its containing window.
tell application "Finder"
	reveal POSIX file "/Users/Shared/report.pdf"
	activate
end tell
