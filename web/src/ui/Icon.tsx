/** A Tabler outline icon. Decorative: anything it means is also said in text or an aria-label. */
export function Icon({ name, className = "" }: { name: string; className?: string }) {
    return <i className={`ti ti-${name} ${className}`} aria-hidden="true" />;
}
