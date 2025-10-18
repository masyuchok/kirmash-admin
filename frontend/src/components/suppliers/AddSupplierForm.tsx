"use client";
import { useState } from "react";

type Props = {
  onSuccess?: () => void;
  onCancel?: () => void;
};

// Подставь свой URL API (ASP.NET)
const API_URL = process.env.NEXT_PUBLIC_BACKEND_URL
  ? `${process.env.NEXT_PUBLIC_BACKEND_URL}/api/suppliers`
  : "/api/suppliers"; // если у тебя есть прокси на next/api

export default function AddSupplierForm({ onSuccess, onCancel }: Props) {
  const [form, setForm] = useState({
    name: "",
    contactName: "",
    website: "",
    country: "",
    city: "",
    currency: "",
    workStart: "", // yyyy-mm-dd
    isVATPayer: false,
    email: "",
    instagram: "",
    phone: "",
    tgContact: "",
  });

  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string>("");

  const handleChange = (e: React.ChangeEvent<HTMLInputElement | HTMLSelectElement>) => {
    const { name, value, type, checked } = e.target;
    setForm((s) => ({ ...s, [name]: type === "checkbox" ? checked : value }));
  };

  const handleSubmit = async (e: React.FormEvent) => {
    e.preventDefault();
    setError("");
    setLoading(true);

    try {
      // Приведём дату к ISO, если заполнена
      const payload = {
        ...form,
        workStart: form.workStart ? new Date(form.workStart + "T00:00:00").toISOString() : null,
      };

      const res = await fetch(API_URL, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(payload),
      });

      if (!res.ok) {
        const txt = await res.text();
        throw new Error(txt || "Ошибка сохранения");
      }

      onSuccess?.();
    } catch (err: any) {
      setError(err?.message ?? "Неизвестная ошибка");
    } finally {
      setLoading(false);
    }
  };

  return (
    <div className="max-w-3xl bg-white p-6 rounded-2xl shadow">
      <h2 className="text-xl font-semibold mb-4">Добавить поставщика</h2>

      <form onSubmit={handleSubmit} className="space-y-4">
        {/* Название */}
        <div>
          <label className="block text-sm font-medium mb-1">Name *</label>
          <input
            required
            name="name"
            value={form.name}
            onChange={handleChange}
            className="w-full border rounded-lg p-2"
          />
        </div>

        {/* Контакт + сайт */}
        <div className="grid grid-cols-1 md:grid-cols-2 gap-4">
          <div>
            <label className="block text-sm font-medium mb-1">ContactName</label>
            <input
              name="contactName"
              value={form.contactName}
              onChange={handleChange}
              className="w-full border rounded-lg p-2"
            />
          </div>
          <div>
            <label className="block text-sm font-medium mb-1">Website</label>
            <input
              name="website"
              type="url"
              value={form.website}
              onChange={handleChange}
              placeholder="https://..."
              className="w-full border rounded-lg p-2"
            />
          </div>
        </div>

        {/* Страна/город */}
        <div className="grid grid-cols-1 md:grid-cols-2 gap-4">
          <div>
            <label className="block text-sm font-medium mb-1">Country</label>
            <input
              name="country"
              value={form.country}
              onChange={handleChange}
              placeholder="PL"
              className="w-full border rounded-lg p-2"
            />
          </div>
          <div>
            <label className="block text-sm font-medium mb-1">City</label>
            <input
              name="city"
              value={form.city}
              onChange={handleChange}
              className="w-full border rounded-lg p-2"
            />
          </div>
        </div>

        {/* Валюта / дата старта / VAT */}
        <div className="grid grid-cols-1 md:grid-cols-3 gap-4">
          <div>
            <label className="block text-sm font-medium mb-1">Currency</label>
            <input
              name="currency"
              value={form.currency}
              onChange={handleChange}
              placeholder="PLN / USD / EUR"
              className="w-full border rounded-lg p-2"
            />
          </div>
          <div>
            <label className="block text-sm font-medium mb-1">WorkStart</label>
            <input
              type="date"
              name="workStart"
              value={form.workStart}
              onChange={handleChange}
              className="w-full border rounded-lg p-2"
            />
          </div>
          <div className="flex items-center gap-2 mt-6 md:mt-8">
            <input
              id="isVATPayer"
              type="checkbox"
              name="isVATPayer"
              checked={form.isVATPayer}
              onChange={handleChange}
              className="h-4 w-4"
            />
            <label htmlFor="isVATPayer" className="text-sm">isVATPayer</label>
          </div>
        </div>

        {/* Контакты */}
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

        <div className="grid grid-cols-1 md:grid-cols-3 gap-4">
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
            <label className="block text-sm font-medium mb-1">TGContact</label>
            <input
              name="tgContact"
              value={form.tgContact}
              onChange={handleChange}
              placeholder="@username"
              className="w-full border rounded-lg p-2"
            />
          </div>
          <div>
            <label className="block text-sm font-medium mb-1">Phone</label>
            <input
              name="phone"
              value={form.phone}
              onChange={handleChange}
              placeholder="+48 ..."
              className="w-full border rounded-lg p-2"
            />
          </div>
        </div>

        {error && <p className="text-red-600 text-sm">{error}</p>}

        <div className="flex gap-3 pt-2">
          <button
            type="submit"
            disabled={loading}
            className="bg-blue-600 text-white px-5 py-2 rounded-xl hover:bg-blue-700 disabled:opacity-50"
          >
            {loading ? "Сохраняю..." : "💾 Сохранить"}
          </button>
          <button
            type="button"
            onClick={onCancel}
            className="px-5 py-2 rounded-xl border"
          >
            Отмена
          </button>
        </div>
      </form>
    </div>
  );
}
