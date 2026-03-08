export default function SectionCard({ title, right, children, className = "" }) {
  return (
    <article className={`panel ${className}`.trim()}>
      {(title || right) && (
        <div className="panel-head">
          <h3 className="panel-title">{title}</h3>
          {right}
        </div>
      )}
      {children}
    </article>
  );
}

