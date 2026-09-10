# Upload progress bar — prototype

Throwaway UI prototype for the upload/send-file progress indicator. The production web client is intentionally not changed.

Run it from the repository root with:

```sh
python3 -m http.server 4173 --directory Docs/prototypes/upload-progress-bar
```

Then open `http://localhost:4173/?variant=a`.

Variants:

- `a` — reference card, close to the supplied screenshot;
- `b` — expanded multi-file queue;
- `c` — compact bottom dock.

The bottom prototype toolbar switches variants and lets you pause, cancel, restart, or scrub the in-memory upload state. This is intentionally not production code.
