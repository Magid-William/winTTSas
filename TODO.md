[x] let's make the url more customizable, some apps asks for server url, and some even asks for endpoint path, we then could do something like `http://127.0.0.1:5178/v1/voice/1/speed/1.2/...` and so on to reflect values
[x] Using `gh` create a new repo, and push
[x] Create a github action for this and when it succeeds, create a release
[x] A `winTTSas setup` wizard: installs to `%LOCALAPPDATA%\Programs\winTTSas`, adds the folder to the user PATH so `winTTSas` works in any terminal, registers natural voices, enables logon start, picks a voice, and adds Start Menu / Apps & Features entries
[x] A per-user MSI with a WiX wizard: installs the files and shortcut, then runs `winTTSas setup --configure` from the Finish page to set up PATH, voices, logon, voice, and the server
