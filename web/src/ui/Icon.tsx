/** Tabler outline. Decorative: its meaning is also said in text or an aria-label. */
export function Icon({ name, className = "" }: { name: string; className?: string }) {
    return <i className={`ti ti-${name} ${className}`} aria-hidden="true" />;
}
