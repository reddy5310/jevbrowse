# Clean Windows checklist (the last gate before the first testers)

For a Windows 10/11 x64 account or VM with **no development tools** and **no `JEVBROWSE_*` or `WEBVIEW2_*` settings**. About 20 minutes. Write down PASS or FAIL and, for a
FAIL, what you saw. Nothing here needs the source code.

**Setup:** copy `JevBrowse-<version>-win-x64.zip` and `SHA256SUMS.txt` to the machine. In PowerShell: `Get-FileHash .\JevBrowse-<version>-win-x64.zip` must equal the hash in
`SHA256SUMS.txt` (and in the release notes). Extract the ZIP to a normal folder (for example `Downloads\JevBrowse`).

| # | Do this | Expect |
|---|---|---|
| 1 | Double-click `JevBrowse.App.exe`. Accept the SmartScreen warning (the build is unsigned): **More info ▸ Run anyway**. | A window opens with the welcome page and four short tips. Press **Next** through them. |
| 2 | Look for the data folder: `%LOCALAPPDATA%\JevBrowse` (paste it into Explorer). | It exists and contains `db`, `profiles`, `ui-prefs.json`. |
| 3 | Open a few sites in tabs (a news site, a video, one you sign in to). Sign in to one real account. | Pages load; sign-in works. |
| 4 | Click a link that opens in a new tab (`target=_blank`). | It opens as a tab. (Pop-up **sign-in** windows that must return to the page are not supported in this alpha.) |
| 5 | Download a file from an ordinary tab, then upload a file (any site with an upload button). | Both work; the file lands where you chose. |
| 6 | Close the window normally, start it again. | The same tab is in front, you are still signed in, and only that tab is awake (others load when opened). |
| 7 | **Site permissions:** on a site that asks for camera, microphone or location, allow it "always". Then **This tab ▾ ▸ Site permissions…** and press **Reset**. Reload. | The permission was listed; after Reset the site asks again. |
| 8 | **This tab ▾ ▸ Clear website data…**, read the dialog, choose Cancel once; then clear. | The dialog names the profile, says you will be signed out, and that downloads and Browser Memory are not deleted. After clearing you are signed out; downloaded files are still there. |
| 9 | Start a **Private** session (Product mode ▸ Private). Open a site. Download a file. | A prompt says the file stays after the session ends. Cancel saves nothing; Continue uses the normal save dialog. |
| 10 | With the Private session still open, close the window. Start JevBrowse again. | No Private tabs or data come back; the ordinary tab you had is in front. |
| 11 | Kill the app from Task Manager (End task). Start it again. | It starts normally and your ordinary tabs are back. |
| 12 | **Offline:** turn the network off, start JevBrowse. | It opens and stays up (pages will not load; Shield lists download later). |
| 13 | **Missing runtime (VM only):** on a machine without the Microsoft Edge WebView2 Runtime, start JevBrowse. | A message names the WebView2 Runtime, offers **Get the WebView2 Runtime**, and **Quit** closes the app. Your data is not touched. |
| 14 | Optional: run `.\scripts\packaged-smoke.ps1 -Zip <zip> -DefaultData` if you have the repository's `scripts` folder. | 0 failures. |

**Backup and restore (also try once):** close JevBrowse, copy the whole `%LOCALAPPDATA%\JevBrowse` folder somewhere, start JevBrowse, change something, close it, put the copy
back, start it: your earlier state returns.

If any row fails, stop and report the row number and what you saw. Do not distribute the build.
