export function PlaceholderPage({ title, phase, description }: { title: string; phase: string; description: string }) {
  return (
    <section className="stack">
      <header className="page-heading"><div><p className="eyebrow">后续阶段 · {phase}</p><h1>{title}</h1><p className="lede">{description}</p></div></header>
      <div className="panel empty-state phase-placeholder">
        <span className="empty-icon" aria-hidden="true">⌁</span>
        <h2>此模块尚未进入当前交付阶段</h2>
        <p>本阶段先完成任务执行、审批和成果验收。当前不显示示例内容或模拟状态。</p>
      </div>
    </section>
  );
}
