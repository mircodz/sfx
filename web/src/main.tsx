import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import "@tabler/icons-webfont/dist/tabler-icons.min.css";
// Latin only (about 130 KB): ids, versions, manifests, and the brand in its cursive italic.
import "@fontsource/cascadia-code/latin-400.css";
import "@fontsource/cascadia-code/latin-500.css";
import "@fontsource/cascadia-code/latin-400-italic.css";
import "./styles/tokens.css";
import "./styles/base.css";
import { App } from "./App";
import { applyTheme, storedTheme } from "./lib/theme";

applyTheme(storedTheme());

createRoot(document.getElementById("root")!).render(
    <StrictMode>
        <App />
    </StrictMode>,
);
