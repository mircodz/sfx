import { createContext, useContext } from "react";

/** A one-line outcome under the top bar; `ok` is false for failures. */
export const NoticeContext = createContext<(text: string, ok: boolean) => void>(() => {});

export const useNotice = () => useContext(NoticeContext);
