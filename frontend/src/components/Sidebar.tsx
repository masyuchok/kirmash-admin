const Sidebar = () => {
  return (
    <aside className="w-64 bg-white shadow-md p-4">
      <nav>
        <ul className="space-y-2">
          <li>
            <a href="/" className="block hover:text-blue-600">
              Аналітыка
            </a>
          </li>
          <li>
            <a href="/suppliers" className="block hover:text-blue-600">
              Пастаўшчыкі
            </a>
          </li>
          <li>
            <a href="/supplies" className="block hover:text-blue-600">
              Пастаўкі
            </a>
          </li>
          <li>
            <a href="/products" className="block hover:text-blue-600">
              Прадукты
            </a>
          </li>
          <li>
            <a href="/sales" className="block hover:text-blue-600">
              Продажы
            </a>
          </li>
          <li>
            <a href="/documents" className="block hover:text-blue-600">
              Дакументы
            </a>
          </li>
        </ul>
      </nav>
    </aside>
  );
};

export default Sidebar;
