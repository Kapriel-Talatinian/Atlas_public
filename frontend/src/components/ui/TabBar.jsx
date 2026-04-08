const TABS = [
  { id: "market", label: "Market" },
  { id: "execution", label: "Execution" },
  { id: "strategy", label: "Strategy Lab" },
  { id: "alpha", label: "Alpha Lab" },
  { id: "polymarket", label: "Polymarket Live" },
  { id: "experimental", label: "Autopilot" },
];

export default function TabBar({ activeTab, onChange }) {
  return (
    <div className="tabbar">
      {TABS.map((tab) => (
        <button
          key={tab.id}
          className={`tabbar-btn ${activeTab === tab.id ? "active" : ""}`}
          onClick={() => onChange(tab.id)}
        >
          {tab.label}
        </button>
      ))}
    </div>
  );
}
