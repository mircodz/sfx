// Renders the component gallery (#/gallery) in light and dark, at desktop and phone widths, plus every dialog and
// panel, and compares each screenshot with its baseline in tests/gallery/. A difference fails and leaves the new
// image next to the baseline as <name>.actual.png to look at; accept it with --update.
//
// Run: bun run build && bun run gallery [--update]
// Baselines depend on the OS's fonts and the Chrome version, so they're checked on the machine that made them; set
// CHROME to a Chrome or Chromium binary if it isn't in the usual place.

import { existsSync, mkdirSync, unlinkSync } from "node:fs";
import { join } from "node:path";

const update = process.argv.includes("--update");
const dist = join(import.meta.dir, "..", "dist");
const baselines = join(import.meta.dir, "..", "tests", "gallery");
mkdirSync(baselines, { recursive: true });

if (!existsSync(join(dist, "index.html"))) {
    console.error("No build to check. Run bun run build first.");
    process.exit(2);
}

const chrome =
    process.env.CHROME ??
    ["/Applications/Google Chrome.app/Contents/MacOS/Google Chrome", "/usr/bin/google-chrome", "/usr/bin/chromium", "/usr/bin/chromium-browser"].find(existsSync);
if (!chrome) {
    console.error("Chrome not found. Set CHROME to a Chrome or Chromium binary.");
    process.exit(2);
}

// Serve the build; the gallery needs no API.
const server = Bun.serve({
    port: 0,
    fetch(req) {
        const path = new URL(req.url).pathname;
        const file = Bun.file(join(dist, path === "/" ? "index.html" : path));
        return file.size ? new Response(file) : new Response("Not found", { status: 404 });
    },
});

const debugPort = 9500 + Math.floor(Math.random() * 400);
const profile = `/tmp/sfx-gallery-${debugPort}`;
const browser = Bun.spawn([chrome, "--headless=new", "--disable-gpu", "--hide-scrollbars", "--force-device-scale-factor=1", `--remote-debugging-port=${debugPort}`, `--user-data-dir=${profile}`, "about:blank"], {
    stdout: "ignore",
    stderr: "ignore",
});
const sleep = (ms: number) => new Promise((r) => setTimeout(r, ms));

type Shot = { name: string; width: number; dark: boolean; open?: string; fullPage: boolean };

const shots: Shot[] = [
    { name: "gallery-light-desktop", width: 1100, dark: false, fullPage: true },
    { name: "gallery-dark-desktop", width: 1100, dark: true, fullPage: true },
    { name: "gallery-light-phone", width: 390, dark: false, fullPage: true },
    { name: "gallery-dark-phone", width: 390, dark: true, fullPage: true },
    ...["Confirm dialog", "Destructive dialog", "Dialog with inputs", "Scale panel", "Application panel"].flatMap((open) => [
        { name: `overlay-${open.toLowerCase().replaceAll(" ", "-")}-light`, width: 1100, dark: false, open, fullPage: false },
        { name: `overlay-${open.toLowerCase().replaceAll(" ", "-")}-dark`, width: 1100, dark: true, open, fullPage: false },
    ]),
];

// Anything that moves or blinks would make screenshots differ from run to run.
const still = `*, *::before, *::after { animation: none !important; transition: none !important; caret-color: transparent !important; }`;

let failed = 0;
try {
    let target: { webSocketDebuggerUrl: string } | undefined;
    for (let i = 0; i < 50 && !target; i++) {
        await sleep(200);
        try {
            target = ((await (await fetch(`http://127.0.0.1:${debugPort}/json`)).json()) as { type: string; webSocketDebuggerUrl: string }[]).find((t) => t.type === "page");
        } catch {
            // Chrome isn't listening yet.
        }
    }
    if (!target) throw new Error("Chrome didn't start.");

    const ws = new WebSocket(target.webSocketDebuggerUrl);
    await new Promise((r) => (ws.onopen = r));
    let id = 0;
    const pending = new Map<number, (v: any) => void>();
    ws.onmessage = (m) => {
        const msg = JSON.parse(String(m.data));
        pending.get(msg.id)?.(msg.result ?? msg);
    };
    const send = (method: string, params: object = {}) =>
        new Promise<any>((resolve) => {
            const n = ++id;
            pending.set(n, resolve);
            ws.send(JSON.stringify({ id: n, method, params }));
        });
    const evaluate = (expression: string) => send("Runtime.evaluate", { expression, awaitPromise: true });

    for (const shot of shots) {
        await send("Emulation.setDeviceMetricsOverride", { width: shot.width, height: 900, deviceScaleFactor: 1, mobile: false });
        await send("Emulation.setEmulatedMedia", { features: [{ name: "prefers-color-scheme", value: shot.dark ? "dark" : "light" }] });
        await send("Page.navigate", { url: `http://localhost:${server.port}/#/gallery` });
        await sleep(800);
        await evaluate(`document.head.insertAdjacentHTML("beforeend", ${JSON.stringify(`<style>${still}</style>`)}); document.fonts.ready.then(() => true)`);
        if (shot.open) {
            await evaluate(`[...document.querySelectorAll("button")].find((b) => b.textContent === ${JSON.stringify(shot.open)})?.click()`);
            await sleep(300);
        }
        let clip;
        if (shot.fullPage) {
            const { cssContentSize } = await send("Page.getLayoutMetrics");
            clip = { x: 0, y: 0, width: shot.width, height: Math.ceil(cssContentSize.height), scale: 1 };
        }
        const { data } = await send("Page.captureScreenshot", { format: "png", captureBeyondViewport: !!clip, clip });
        const png = Buffer.from(data, "base64");

        const baseline = join(baselines, `${shot.name}.png`);
        const actual = join(baselines, `${shot.name}.actual.png`);
        if (update || !existsSync(baseline)) {
            await Bun.write(baseline, png);
            if (existsSync(actual)) unlinkSync(actual);
            console.log(`${update ? "updated" : "new"}   ${shot.name}`);
        } else if (Buffer.compare(png, Buffer.from(await Bun.file(baseline).arrayBuffer())) !== 0) {
            await Bun.write(actual, png);
            failed++;
            console.log(`changed ${shot.name}: compare ${shot.name}.png with ${shot.name}.actual.png`);
        } else {
            if (existsSync(actual)) unlinkSync(actual);
            console.log(`same    ${shot.name}`);
        }
    }
    ws.close();
} finally {
    browser.kill();
    server.stop(true);
    await Bun.spawn(["rm", "-rf", profile]).exited;
}

if (failed > 0) {
    console.log(`\n${failed} of ${shots.length} screenshots changed. If the change is intended, run bun run gallery --update.`);
    process.exit(1);
}
console.log(`\nAll ${shots.length} screenshots match.`);
