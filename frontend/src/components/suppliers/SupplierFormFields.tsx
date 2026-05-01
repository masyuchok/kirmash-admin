import type { SupplierFormValues } from '@/lib/suppliers/supplierFormTypes';

const inputClass =
  'w-full rounded-lg border border-gray-200 bg-white px-3 py-2 text-sm text-gray-900 shadow-sm placeholder:text-gray-400 focus-visible:border-primary focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary/25';

type Props = {
  values: SupplierFormValues;
  onChange: (next: SupplierFormValues) => void;
  /** Extra hint under the name field (create flow). */
  showDuplicateNameHint?: boolean;
};

export default function SupplierFormFields({
  values,
  onChange,
  showDuplicateNameHint,
}: Props) {
  const handleChange = (
    e: React.ChangeEvent<HTMLInputElement | HTMLSelectElement>
  ) => {
    const el = e.currentTarget;
    const { name } = el;
    const value =
      el instanceof HTMLInputElement && el.type === 'checkbox'
        ? el.checked
        : el.value;
    const key = name as keyof SupplierFormValues;
    onChange({ ...values, [key]: value } as SupplierFormValues);
  };

  return (
    <div className="space-y-4">
      <div>
        <label className="mb-1 block text-sm font-medium text-gray-700">Назва*</label>
        <input
          name="name"
          required
          value={values.name}
          onChange={handleChange}
          placeholder="Назва"
          className={inputClass}
        />
        {showDuplicateNameHint && (
          <p className="mt-1 text-xs text-gray-500">Будзе праверка на дублікаты па назве</p>
        )}
      </div>

      <div className="grid grid-cols-1 gap-4 md:grid-cols-2">
        <div>
          <label className="mb-1 block text-sm font-medium text-gray-700">
            Кантактная асоба
          </label>
          <input
            name="contactName"
            value={values.contactName}
            onChange={handleChange}
            placeholder="Імя кантактнай асобы"
            className={inputClass}
          />
        </div>
        <div>
          <label className="mb-1 block text-sm font-medium text-gray-700">Сайт</label>
          <input
            type="url"
            name="website"
            value={values.website}
            onChange={handleChange}
            placeholder="https://..."
            className={inputClass}
          />
        </div>
      </div>

      <div className="grid grid-cols-1 gap-4 md:grid-cols-2">
        <div>
          <label className="mb-1 block text-sm font-medium text-gray-700">Краіна</label>
          <input
            name="country"
            value={values.country}
            onChange={handleChange}
            placeholder="Польшча"
            className={inputClass}
          />
        </div>
        <div>
          <label className="mb-1 block text-sm font-medium text-gray-700">Горад</label>
          <input
            name="city"
            value={values.city}
            onChange={handleChange}
            placeholder="Варшава"
            className={inputClass}
          />
        </div>
      </div>

      <div className="grid grid-cols-1 gap-4 md:grid-cols-3">
        <div>
          <label className="mb-1 block text-sm font-medium text-gray-700">Валюта*</label>
          <input
            name="currency"
            required
            value={values.currency}
            onChange={handleChange}
            placeholder="PLN / EUR / USD / BYN"
            className={inputClass}
          />
        </div>
        <div>
          <label className="mb-1 block text-sm font-medium text-gray-700">
            Дата пачатку супрацоўніцтва
          </label>
          <input
            type="date"
            name="workStart"
            value={values.workStart}
            onChange={handleChange}
            className={inputClass}
          />
        </div>
        <label className="mt-7 flex items-center gap-2 md:mt-8">
          <input
            type="checkbox"
            name="isVATPayer"
            checked={values.isVATPayer}
            onChange={handleChange}
            className="size-4 rounded border-gray-300 text-primary focus:ring-primary"
          />
          <span className="text-sm text-gray-700">Плаціць ВАТы</span>
        </label>
      </div>

      <div className="grid grid-cols-1 gap-4 md:grid-cols-3">
        <div>
          <label className="mb-1 block text-sm font-medium text-gray-700">Email</label>
          <input
            type="email"
            name="email"
            value={values.email}
            onChange={handleChange}
            className={inputClass}
          />
        </div>
        <div>
          <label className="mb-1 block text-sm font-medium text-gray-700">Instagram</label>
          <input
            name="instagram"
            value={values.instagram}
            onChange={handleChange}
            placeholder="@handle"
            className={inputClass}
          />
        </div>
        <div>
          <label className="mb-1 block text-sm font-medium text-gray-700">Telegram</label>
          <input
            name="tgContact"
            value={values.tgContact}
            onChange={handleChange}
            placeholder="@username"
            className={inputClass}
          />
        </div>
      </div>

      <div>
        <label className="mb-1 block text-sm font-medium text-gray-700">Тэлефон</label>
        <input
          name="phone"
          value={values.phone}
          onChange={handleChange}
          placeholder="+48 ..."
          className={inputClass}
        />
      </div>
    </div>
  );
}
