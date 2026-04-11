const Sidebar = () => {
  return (
    <aside className="w-64 bg-white shadow-md p-4">
      <nav>
        <ul className="space-y-2">
          <li>
            <a href="/" className="block hover:text-primary">
              Аналітыка
            </a>
          </li>
          <li>
            <a href="/suppliers" className="block hover:text-primary">
              Пастаўшчыкі
            </a>
          </li>
          <li>
            <a href="/supplies" className="block hover:text-primary">
              Пастаўкі
            </a>
          </li>
          <li>
            <a href="/products" className="block hover:text-primary">
              Прадукты
            </a>
          </li>
          <li>
            <a href="/sales" className="block hover:text-primary">
              Продажы
            </a>
          </li>
          <li>
            <a href="/documents" className="block hover:text-primary">
              Дакументы
            </a>
          </li>
        </ul>
      </nav>
    </aside>
  );
};

export default Sidebar;
