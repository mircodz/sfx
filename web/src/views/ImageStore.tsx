import { useState } from "react";
import { ActionGroup } from "../actions/ActionGroup";
import { ProvisionPanel } from "../actions/ProvisionPanel";
import { api } from "../lib/api";
import { bytes, natural, plural, when } from "../lib/format";
import { useResource } from "../lib/resource";
import { useQueryParam } from "../lib/router";
import { useSession } from "../lib/session";
import type { StoreFile, StoreFolder, StoreListing, StoreUse } from "../lib/types";
import { Button } from "../ui/Button";
import { Icon } from "../ui/Icon";
import { Load } from "../ui/Load";
import { ProgressBar } from "../ui/Meter";
import { PageHeader } from "../ui/PageHeader";
import { Empty, Section } from "../ui/Section";
import { Limited } from "../ui/ShowMore";
import { SearchBox, Toolbar } from "../ui/Toolbar";
import "./ImageStore.css";

const at = (path: string) => (path ? `#/store?path=${encodeURIComponent(path)}` : "#/store");

/** Packages waiting to be provisioned (or left behind after it), and the copies Service Fabric keeps under Store. */
export function ImageStore() {
    const [path] = useQueryParam("path", "");
    const listing = useResource(`store:${path}`, () => api.store(path));
    const session = useSession();
    const [search, setSearch] = useState("");
    const [uploading, setUploading] = useState(false);
    const parts = path.split("/").filter(Boolean);

    return (
        <Load resource={listing}>
            {(l) => {
                const q = search.toLowerCase();
                const folders = l.folders.filter((f) => !q || f.name.toLowerCase().includes(q)).sort((a, b) => natural(a.name, b.name));
                const files = l.files.filter((f) => !q || f.name.toLowerCase().includes(q)).sort((a, b) => natural(a.name, b.name));
                const packages = l.folders.filter((f) => f.package);
                return (
                    <>
                        <h2 className="sr-only">
                            {path ? `Folder ${path}` : "The image store"}: {plural(l.folders.length, "folder")}, {plural(l.files.length, "file")}.
                        </h2>
                        <PageHeader
                            title={parts.length ? parts[parts.length - 1] : "Image store"}
                            crumbs={parts.length ? [{ label: "Image store", href: at("") }, ...parts.slice(0, -1).map((p, i) => ({ label: p, href: at(parts.slice(0, i + 1).join("/")) }))] : undefined}
                            summary={[
                                l.kind === "Native" ? "Native image store" : l.kind === "FileShare" ? "File share" : l.kind === "AzureBlob" ? "Azure blob storage" : null,
                                plural(l.folders.length, "folder"),
                                plural(l.files.length, "file"),
                                packages.length ? plural(packages.length, "package") : null,
                            ]
                                .filter(Boolean)
                                .join(" · ")}
                            onRefresh={listing.reload}
                            updated={listing.at}
                            actions={
                                !session?.readOnly && (
                                    <Button variant="primary" onClick={() => setUploading(true)}>
                                        Upload a package
                                    </Button>
                                )
                            }
                        />
                        {!path && <Usage usage={l.usage} />}
                        {(l.folders.length + l.files.length > 12 || q) && (
                            <Toolbar>
                                <SearchBox value={search} onChange={setSearch} placeholder="Filter by name" />
                            </Toolbar>
                        )}
                        <Section label={path ? "In this folder" : "At the root"} aside={q ? `${folders.length + files.length} of ${l.folders.length + l.files.length}` : undefined}>
                            {l.folders.length + l.files.length === 0 && <Empty>This folder is empty.</Empty>}
                            {l.folders.length + l.files.length > 0 && folders.length + files.length === 0 && <Empty>Nothing matches this filter.</Empty>}
                            <Limited items={folders}>{(f) => <FolderRow key={f.path} folder={f} />}</Limited>
                            <Limited items={files}>{(f) => <FileRow key={f.path} file={f} />}</Limited>
                        </Section>
                        {uploading && <ProvisionPanel onClose={() => setUploading(false)} />}
                    </>
                );
            }}
        </Load>
    );
}

/** Only where the cluster reports it (native image store, 7.1 and later). */
function Usage({ usage }: { usage: StoreListing["usage"] }) {
    if (!usage?.diskCapacity) return null;
    const used = usage.diskCapacity - (usage.diskAvailable ?? 0);
    const pct = Math.round((used / usage.diskCapacity) * 100);
    const use = (u: StoreUse | null | undefined, what: string) =>
        !!u?.bytes && `${bytes(u.bytes)} ${what}${u.files != null ? ` (${plural(u.files, "file")})` : ""}`;
    const parts = [use(usage.register, "provisioned"), use(usage.copy, "copied"), use(usage.staging, "uploading")].filter(Boolean);
    return (
        <Section label="Disk">
            <div className="meter-head">
                <span>
                    {bytes(used)} of {bytes(usage.diskCapacity)} used
                </span>
                <span>{pct}%</span>
            </div>
            <ProgressBar pct={pct} label={`Image store disk ${pct}% used`} warnAbove={85} />
            {parts.length > 0 && <p className="secondary small store-usage">{parts.join(" · ")}</p>}
        </Section>
    );
}

/** Its size loads on its own, as the store only reports it per folder. */
function FolderRow({ folder: f }: { folder: StoreFolder }) {
    const size = useResource(`store-size:${f.path}`, () => (f.size != null ? Promise.resolve({ size: f.size }) : api.storeSize(f.path)), false);
    const pkg = f.package;
    return (
        <div className="row store-row">
            <a className="row-summary" href={at(f.path)}>
                <Icon name={pkg ? "package" : "folder"} className="row-icon" />
                <span className="row-main">
                    <span className="row-name">{f.name}</span>
                    <span className="row-sub">
                        {[
                            pkg && [pkg.typeName && pkg.typeVersion ? `${pkg.typeName} ${pkg.typeVersion}` : "Application package", pkg.provisioned ? (pkg.status && pkg.status !== "Available" ? pkg.status.toLowerCase() : "provisioned") : "not provisioned"].join(" · "),
                            f.reserved && "Service Fabric's own",
                            f.fileCount != null && plural(f.fileCount, "file"),
                        ]
                            .filter(Boolean)
                            .join(" · ") || "Folder"}
                    </span>
                </span>
                <span className="row-value">{size.data?.size != null ? bytes(size.data.size) : size.loading ? "…" : ""}</span>
            </a>
            {f.actions.length > 0 && (
                <div className="store-row-actions">
                    <ActionGroup actions={f.actions} label={`Actions for ${f.name}`} />
                </div>
            )}
        </div>
    );
}

function FileRow({ file: f }: { file: StoreFile }) {
    return (
        <div className="row store-row">
            <div className="row-summary store-file">
                <Icon name="file" className="row-icon" />
                <span className="row-main">
                    <span className="row-name">{f.name}</span>
                    <span className="row-sub">{[f.version && `version ${f.version}`, f.modified && `changed ${when(f.modified)}`].filter(Boolean).join(" · ") || "File"}</span>
                </span>
                <span className="row-value">{f.size != null ? bytes(f.size) : ""}</span>
            </div>
            {f.actions.length > 0 && (
                <div className="store-row-actions">
                    <ActionGroup actions={f.actions} label={`Actions for ${f.name}`} />
                </div>
            )}
        </div>
    );
}
