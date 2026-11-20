// Checks the rules of docs/style.md that a machine can check, so they hold without anyone remembering them.
// Run: bun run check

import { Glob } from "bun";

type Rule = { name: string; files: string; test: (line: string, file: string) => boolean; allow?: string[] };

const rules: Rule[] = [
    {
        name: "Colors come from tokens.css (no hex or rgb values elsewhere)",
        files: "src/**/*.css",
        test: (line, file) => !file.endsWith("styles/tokens.css") && /#[0-9a-f]{3,8}\b|rgba?\(|hsla?\(/i.test(line),
    },
    {
        name: "One emphasis weight: 500 (never 600, 700 or bold)",
        files: "src/**/*.{css,tsx}",
        test: (line) => /font-weight:\s*(600|700|800|bold)|fontWeight:\s*(600|700|"bold")/.test(line),
    },
    {
        name: "No inline styles except data-driven sizes",
        files: "src/**/*.tsx",
        test: (line) => line.includes("style={{"),
        allow: ["src/ui/Meter.tsx", "src/ui/Overlay.tsx", "src/ui/HealthBar.tsx"],
    },
    {
        name: "Messages say what happened, without an 'Error:' prefix",
        files: "src/**/*.{ts,tsx}",
        test: (line) => /["'`]Error: /.test(line),
    },
    {
        name: "No emoji; icons are Tabler outline only (no -filled)",
        files: "src/**/*.{ts,tsx}",
        test: (line) => /\p{Extended_Pictographic}/u.test(line) || /ti-[\w-]+-filled|icon="[\w-]+-filled"/.test(line),
    },
    {
        name: "Sentence case on buttons (no Title Case of two or more capitalized words)",
        files: "src/**/*.tsx",
        test: (line) => {
            const m = line.match(/<Button[^>]*>([^<{]+)</);
            return !!m && /\b[A-Z][a-z]+ [A-Z][a-z]+/.test(m[1].trim());
        },
    },
];

let failures = 0;
for (const rule of rules) {
    for await (const file of new Glob(rule.files).scan(".")) {
        if (rule.allow?.includes(file)) continue;
        const lines = (await Bun.file(file).text()).split("\n");
        lines.forEach((line, i) => {
            if (line.trim().startsWith("//") || line.trim().startsWith("/*") || line.trim().startsWith("*")) return;
            if (rule.test(line, file)) {
                failures++;
                console.log(`${file}:${i + 1}: ${rule.name}\n    ${line.trim()}`);
            }
        });
    }
}

if (failures > 0) {
    console.log(`\n${failures} ${failures === 1 ? "place breaks" : "places break"} the style rules (docs/style.md).`);
    process.exit(1);
}
console.log(`Style rules hold (${rules.length} rules).`);
