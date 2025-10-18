'use client';
import { useState } from 'react';

type Props = {
  onSuccess?: (createdId: number) => void;
  onCancel?: () => void;
};

const API_URL = `${process.env.NEXT_PUBLIC_API_URL}/suppliers/add`;

const AddSupplierForm = ({ onSuccess, onCancel }: Props) => {
  const [form, setForm] = useState({
    name: '',
    contactName: '',
    website: '',
    country: '',
    city: '',
    currency: 'PLN',
    workStart: new Date().toISOString().split('T')[0], // yyyy-mm-dd
    isVATPayer: false,
    email: '',
    instagram: '',
    phone: '',
    tgContact: '',
  });
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string>('');

  const handleChange = (
    e: React.ChangeEvent<HTMLInputElement | HTMLSelectElement>
  ) => {
    const el = e.currentTarget;
    const { name } = el;
    const value =
      el instanceof HTMLInputElement && el.type === 'checkbox'
        ? el.checked
        : el.value;
    setForm((s) => ({ ...s, [name]: value }));
  };

  const submit = async (e: React.FormEvent) => {
    e.preventDefault();
    setError('');
    setLoading(true);
    try {
      const payload = {
        ...form,
        workStart: form.workStart,
      };

      const res = await fetch(API_URL, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        credentials: 'include',
        body: JSON.stringify(payload),
      });

      if (!res.ok) {
        let msg = 'Памылка пры захаванні';
        try {
          const data = await res.json();
          msg = data?.error || data?.message || msg;
        } catch {
          msg = await res.text().catch(() => msg);
        }
        throw new Error(msg);
      }

      const data = await res.json().catch(() => ({}));
      const createdId = typeof data?.id === 'number' ? data.id : undefined;

      onSuccess?.(createdId ?? -1);
    } catch (err: any) {
      setError(err?.message ?? 'Невядомая памылка');
    } finally {
      setLoading(false);
    }
  };

  return (
    <div className="max-w-3xl bg-white p-6 rounded-2xl shadow">
      <h2 className="text-xl font-semibold mb-4">Дадаць пастаўшчыка</h2>

      <form onSubmit={submit} className="space-y-4">
        <div>
          <label className="block text-sm font-medium mb-1">Назва*</label>
          <input
            name="name"
            required
            value={form.name}
            onChange={handleChange}
            placeholder="Назва"
            className="w-full border rounded-lg p-2"
          />
          <p className="text-xs text-gray-500 mt-1">
            Будзе праверка на дублікаты па назве
          </p>
        </div>

        <div className="grid grid-cols-1 md:grid-cols-2 gap-4">
          <div>
            <label className="block text-sm font-medium mb-1">
              Кантактная асоба
            </label>
            <input
              name="contactName"
              value={form.contactName}
              onChange={handleChange}
              placeholder="Імя кантактная асобы"
              className="w-full border rounded-lg p-2"
            />
          </div>
          <div>
            <label className="block text-sm font-medium mb-1">Сайт</label>
            <input
              type="url"
              name="website"
              value={form.website}
              onChange={handleChange}
              placeholder="https://..."
              className="w-full border rounded-lg p-2"
            />
          </div>
        </div>

        <div className="grid grid-cols-1 md:grid-cols-2 gap-4">
          <div>
            <label className="block text-sm font-medium mb-1">Краіна</label>
            <input
              name="country"
              value={form.country}
              onChange={handleChange}
              placeholder="Польшча"
              className="w-full border rounded-lg p-2"
            />
          </div>
          <div>
            <label className="block text-sm font-medium mb-1">Горад</label>
            <input
              name="city"
              value={form.city}
              onChange={handleChange}
              placeholder="Варшава"
              className="w-full border rounded-lg p-2"
            />
          </div>
        </div>

        <div className="grid grid-cols-1 md:grid-cols-3 gap-4">
          <div>
            <label className="block text-sm font-medium mb-1">Валюта*</label>
            <input
              name="currency"
              required
              value={form.currency}
              onChange={handleChange}
              placeholder="PLN / EUR / USD / BYN"
              className="w-full border rounded-lg p-2"
            />
          </div>
          <div>
            <label className="block text-sm font-medium mb-1">
              Дата пачатку супрацоўніцтва
            </label>
            <input
              type="date"
              name="workStart"
              value={form.workStart}
              onChange={handleChange}
              className="w-full border rounded-lg p-2"
            />
          </div>
          <label className="flex items-center gap-2 mt-7 md:mt-8">
            <input
              type="checkbox"
              name="isVATPayer"
              checked={form.isVATPayer}
              onChange={handleChange}
              className="h-4 w-4"
            />
            <span className="text-sm">Плаціць ВАТы</span>
          </label>
        </div>

        <div className="grid grid-cols-1 md:grid-cols-3 gap-4">
          <div>
            <label className="block text-sm font-medium mb-1">Email</label>
            <input
              type="email"
              name="email"
              value={form.email}
              onChange={handleChange}
              className="w-full border rounded-lg p-2"
            />
          </div>
          <div>
            <label className="block text-sm font-medium mb-1">Instagram</label>
            <input
              name="instagram"
              value={form.instagram}
              onChange={handleChange}
              placeholder="@handle"
              className="w-full border rounded-lg p-2"
            />
          </div>
          <div>
            <label className="block text-sm font-medium mb-1">Telegram</label>
            <input
              name="tgContact"
              value={form.tgContact}
              onChange={handleChange}
              placeholder="@username"
              className="w-full border rounded-lg p-2"
            />
          </div>
        </div>

        <div>
          <label className="block text-sm font-medium mb-1">Тэлефон</label>
          <input
            name="phone"
            value={form.phone}
            onChange={handleChange}
            placeholder="+48 ..."
            className="w-full border rounded-lg p-2"
          />
        </div>

        {error && <p className="text-sm text-red-600">{error}</p>}

        <div className="flex gap-3 pt-2">
          <button
            type="submit"
            disabled={loading}
            className="bg-blue-600 text-white px-5 py-2 rounded-xl hover:bg-blue-700 disabled:opacity-50"
          >
            {loading ? 'Захоўваю...' : '💾 Дадаць'}
          </button>
          <button
            type="button"
            onClick={onCancel}
            className="px-5 py-2 rounded-xl border"
          >
            Адмена
          </button>
        </div>
      </form>
    </div>
  );
};

export default AddSupplierForm;
