import { createContext, useContext } from "react";

/** Shows a one-line outcome under the top bar: ok for what was requested or done, not ok for failures. */
export const NoticeContext = createContext<(text: string, ok: boolean) => void>(() => {});

export const useNotice = () => useContext(NoticeContext);
