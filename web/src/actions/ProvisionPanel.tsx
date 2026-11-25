import { useRef, useState } from "react";
import { api } from "../lib/api";
import { bytes, plural } from "../lib/format";
import { useNotice } from "../lib/notice";
import { Button } from "../ui/Button";
import { Fields } from "../ui/Fields";
import { Checkbox } from "../ui/Form";
import { Notice } from "../ui/Notice";
import { SidePanel } from "../ui/Overlay";
import "./actions.css";

type Picked = {
    files: File[];
    label: string;
    size: number;
    /** From the folder's ApplicationManifest.xml; a zip is read on the server. */
    typeName?: string;
    typeVersion?: string;
    problem?: string;
};

/** The package root is the shallowest ApplicationManifest.xml in the chosen folder. */
async function readFolder(files: File[]): Promise<Picked> {
    const size = files.reduce((n, f) => n + f.size, 0);
    const label = files[0]?.webkitRelativePath.split("/")[0] ?? "Folder";
    const manifest = files
        .filter((f) => f.name === "ApplicationManifest.xml")
        .sort((a, b) => a.webkitRelativePath.split("/").length - b.webkitRelativePath.split("/").length)[0];
    if (!manifest) return { files, label, size, problem: "There's no ApplicationManifest.xml in this folder, so it isn't an application package." };
    const xml = new DOMParser().parseFromString(await manifest.text(), "application/xml");
    const root = xml.documentElement;
    const typeName = root?.getAttribute("ApplicationTypeName") ?? undefined;
    const typeVersion = root?.getAttribute("ApplicationTypeVersion") ?? undefined;
    if (!typeName || !typeVersion) return { files, label, size, problem: "ApplicationManifest.xml doesn't name the application type and version." };
    return { files, label, size, typeName, typeVersion };
}

/** The upload runs on the server and shows its progress in Activity; this panel only picks and reviews. */
export function ProvisionPanel({ onClose }: { onClose: () => void }) {
    const notify = useNotice();
    const folder = useRef<HTMLInputElement>(null);
    const file = useRef<HTMLInputElement>(null);
    const [picked, setPicked] = useState<Picked | null>(null);
    const [provision, setProvision] = useState(true);
    const [sending, setSending] = useState(false);
    const [error, setError] = useState<string | null>(null);

    const pickFolder = async (list: FileList | null) => {
        if (!list?.length) return;
        setError(null);
        setPicked(await readFolder([...list]));
    };
    const pickFile = (list: FileList | null) => {
        const f = list?.[0];
        if (!f) return;
        setError(null);
        setPicked(/\.(sfpkg|zip)$/i.test(f.name) ? { files: [f], label: f.name, size: f.size } : { files: [f], label: f.name, size: f.size, problem: "Choose an .sfpkg or .zip file, or a package folder." });
    };

    const submit = async (close: () => void) => {
        if (!picked || picked.problem) return;
        const form = new FormData();
        form.append("provision", String(provision));
        // A folder's files keep their path inside it; the server finds the package root from the manifest.
        for (const f of picked.files) form.append("files", f, f.webkitRelativePath || f.name);
        setSending(true);
        setError(null);
        try {
            const result = await api.upload(form);
            notify(result.message, true);
            close();
        } catch (e) {
            setError(e instanceof Error ? e.message : String(e));
            setSending(false);
        }
    };

    return (
        <SidePanel
            title="Provision a package"
            subtitle={<p className="secondary small">Uploads it to the image store, then registers its application type so applications can be created from it.</p>}
            onClose={onClose}
            onCancel={() => !sending}
            footer={(close) => (
                <>
                    <Button variant="primary" disabled={!picked || !!picked.problem || sending} onClick={() => submit(close)}>
                        {sending ? "Sending…" : provision ? "Upload and provision" : "Upload"}
                    </Button>
                    <Button onClick={close} disabled={sending}>
                        Cancel
                    </Button>
                </>
            )}
        >
            <div className="provision-pick">
                <Button onClick={() => folder.current?.click()} disabled={sending}>
                    Choose a folder
                </Button>
                <Button onClick={() => file.current?.click()} disabled={sending}>
                    Choose an .sfpkg or .zip
                </Button>
                {/* webkitdirectory isn't in React's types; every current browser supports it. */}
                <input ref={folder} type="file" hidden {...{ webkitdirectory: "", directory: "" }} onChange={(e) => pickFolder(e.target.files)} />
                <input ref={file} type="file" hidden accept=".sfpkg,.zip" onChange={(e) => pickFile(e.target.files)} />
            </div>
            {!picked && <p className="secondary">A package is the folder with ApplicationManifest.xml at its root, as Visual Studio or a build packages it.</p>}
            {picked && (
                <>
                    <Fields
                        fields={[
                            { name: "Package", value: picked.label },
                            { name: "Application type", value: picked.typeName ?? (picked.problem ? null : "Read from the package when it's uploaded") },
                            { name: "Version", value: picked.typeVersion ?? null },
                            { name: "Size", value: `${bytes(picked.size)}${picked.files.length > 1 ? ` in ${plural(picked.files.length, "file")}` : ""}` },
                        ]}
                    />
                    {picked.problem && <Notice tone="danger">{picked.problem}</Notice>}
                </>
            )}
            <Checkbox label="Provision it once it's uploaded" checked={provision} onChange={setProvision} disabled={sending} />
            {error && <Notice tone="danger">{error}</Notice>}
        </SidePanel>
    );
}
