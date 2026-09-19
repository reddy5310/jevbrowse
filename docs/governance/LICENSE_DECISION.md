# License decision (for the maintainer)

Constraints from the architecture: open source; forks must remain legally possible; the JevBrowse name/logo are protected separately; community adoption matters; contributions should flow back.

| Option | What it means for others | What it means for you | Fit |
|---|---|---|---|
| **MPL-2.0** (recommended) | Anyone can use, fork, and even embed JevBrowse files in proprietary products, but **changes to our files must be published**. File-level copyleft. Compatible with GPL and Apache code. Used by Firefox, Thunderbird. | You keep the strongest practical guarantee that improvements to the core come back, without scaring off companies who want to build on it. Simple CLA-free contribution. | Best match for "open source + anti-bloat + forks possible" |
| Apache-2.0 | Fully permissive; includes an explicit patent grant; forks can go closed. | Maximum adoption; you may see closed forks with your engine and no changes returned. | Good if adoption matters more than reciprocity |
| AGPL-3.0 | Strong copyleft incl. network use; any derivative must be AGPL. | Most protective; many companies refuse AGPL entirely, and it complicates a future MSIX/Store distribution and library reuse. | Too heavy for a browser meant to be embedded/forked |
| GPL-3.0 | Strong copyleft (no network clause). | Similar company aversion; Apache code can be included but not vice versa. | Middle ground, less common for browsers |

Recommendation: **MPL-2.0** for the code, plus `TRADEMARK.md` stating that "JevBrowse" and the logo may not be used for modified builds without permission (forks must rename), which is exactly what Mozilla does with Firefox/Iceweasel. This satisfies GOVERNANCE.md's "separate the license from the name".

To apply: say "MPL-2.0" (or another) and I will add `LICENSE`, `TRADEMARK.md`, the SPDX header to `Directory.Build.props`, and update README/GOVERNANCE. Nothing is granted until the file exists.
