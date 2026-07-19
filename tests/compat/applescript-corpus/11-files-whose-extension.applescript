-- whose-filter: every file with a given extension.
tell application "Finder" to get every file of folder "Documents" of home whose name extension is "txt"
