/** JARVIS's visual presence. The animation reflects what JARVIS is doing. */
export function Orb({ state, size = 48 }: { state: string; size?: number }) {
  const cls = `orb orb-${state.toLowerCase()}`;
  return (
    <div className={cls} style={{ width: size, height: size }} role="img" aria-label={`JARVIS: ${state}`}>
      <div className="orb-glow" />
      <div className="orb-ring" />
      <div className="orb-core" />
    </div>
  );
}
