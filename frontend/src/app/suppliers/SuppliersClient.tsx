'use client';
import { useEffect, useState } from 'react';
import { useTopbar } from '@/components/topbar/TopbarContext';
import { FiPlus, FiPackage, FiFileText } from 'react-icons/fi';

// import { Plus, List } from 'lucide-react';
// import AddSupplierForm from "./AddSupplierForm";
// import SuppliersTable from "./SuppliersTable";

enum ViewMode {
  Default = 'default',
  AddSupplier = 'addSupplier',
  Inventory = 'inventory',
  Documents = 'documents',
}

export default function SuppliersClient() {
  const [mode, setMode] = useState<ViewMode>(ViewMode.Default);
  const [editingId, setEditingId] = useState<number | null>(null);
  const { setTopbarButtons } = useTopbar();

  useEffect(() => {
    setTopbarButtons([
      {
        label: 'Новы пастаўшчык',
        icon: <FiPlus />,
        onClick: () => setMode(ViewMode.AddSupplier),
      },
      {
        label: 'Інвентарызацыя',
        icon: <FiPackage />,
        onClick: () => setMode(ViewMode.Inventory),
      },
      {
        label: 'Дакументы',
        icon: <FiFileText />,
        onClick: () => setMode(ViewMode.Documents),
      },
    ]);
    return () => setTopbarButtons([]);
  }, [setTopbarButtons]);

  switch (mode) {
      case ViewMode.AddSupplier:
        return <div className="p-4"></div>;
      case ViewMode.Inventory:
        return <div>📦 Тут будзе інвентарызацыя</div>;
      case ViewMode.Documents:
        return <div>📁 Тут будуць дакументы</div>;
      default:
        return <div>📃 Агульны спіс пастаўшчыкоў</div>;
    }

  if (mode === ViewMode.AddSupplier) {
    return (
      <div>
        <span>Add</span>
      </div>
      //   <AddSupplierForm
      //     onSuccess={() => setMode('list')}
      //     onCancel={() => setMode('list')}
      //   />
    );
  }

  //   if (mode === 'edit' && editingId) {
  //     // можно подключить твою форму редактирования
  //     return (
  //       <div className="max-w-3xl bg-white p-6 rounded-2xl shadow">
  //         Форма редактирования (todo)
  //       </div>
  //     );
  //   }

  return (
    <div className="">
      <span>List</span>
      {/* <SuppliersTable
        onEdit={(id) => {
          setEditingId(id);
          setMode('edit');
        }}
      /> */}
    </div>
  );
}
