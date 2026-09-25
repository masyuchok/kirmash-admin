'use client';

import { useMemo, useState } from 'react';
import { FiExternalLink, FiX } from 'react-icons/fi';
import {
  confirmBookOcrAndSearch,
  createBookFromLookup,
  lookupBookByPhoto,
  lookupBookFromPhoto,
  lookupBookFromText,
  lookupBookFromUrl,
  lookupBookNext,
  type BookLookupCandidate,
  type BookLookupStepResult,
} from '@/lib/api/bookLookup';
import { compressImageForUpload } from '@/lib/images/compressImageForUpload';

type Step =
  | 'menu'
  | 'photo'
  | 'text'
  | 'ocrConfirm'
  | 'confirm'
  | 'edit'
  | 'exhausted'
  | 'busy';

type Props = {
  open: boolean;
  supplierId?: string;
  onClose: () => void;
  onCreated: (product: {
    shopifyProductId: string;
    shopifyVariantId: string;
    title: string;
  }) => void;
  onOpenShopifyManual: () => void;
};

const inputClass =
  'w-full rounded-lg border border-gray-200 bg-white px-3 py-2 text-sm text-gray-900 shadow-sm placeholder:text-gray-400 focus-visible:border-primary focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary/25';

function sourceLabel(source: string): string {
  if (source === 'supplier') return 'сайт пастаўшчыка';
  if (source === 'image') return 'пошук па фота';
  if (source === 'manual') return 'ручная спасылка';
  return 'інтэрнэт';
}

export default function BookAiLookupModal({
  open,
  supplierId,
  onClose,
  onCreated,
  onOpenShopifyManual,
}: Props) {
  const [step, setStep] = useState<Step>('menu');
  const [error, setError] = useState<string | null>(null);
  const [coverFile, setCoverFile] = useState<File | null>(null);
  const [isbnFile, setIsbnFile] = useState<File | null>(null);
  const [textTitle, setTextTitle] = useState('');
  const [textIsbn, setTextIsbn] = useState('');
  const [sessionId, setSessionId] = useState('');
  const [candidate, setCandidate] = useState<BookLookupCandidate | null>(null);
  const [attemptsUsed, setAttemptsUsed] = useState(0);
  const [attemptsMax, setAttemptsMax] = useState(0);
  const [doneMessage, setDoneMessage] = useState<string | null>(null);
  const [canSearchByPhoto, setCanSearchByPhoto] = useState(false);
  const [photoSearchExhausted, setPhotoSearchExhausted] = useState(false);
  const [manualUrl, setManualUrl] = useState('');
  const [editTitle, setEditTitle] = useState('');
  const [editAuthor, setEditAuthor] = useState('');
  const [editIsbn, setEditIsbn] = useState('');
  const [editPublisher, setEditPublisher] = useState('');
  const [salePrice, setSalePrice] = useState('0');
  const [unitCost, setUnitCost] = useState('0');
  const [ocrTitle, setOcrTitle] = useState('');
  const [ocrAuthor, setOcrAuthor] = useState('');
  const [ocrIsbn, setOcrIsbn] = useState('');
  const [hasCoverInSession, setHasCoverInSession] = useState(false);

  const coverPreview = useMemo(
    () => (coverFile ? URL.createObjectURL(coverFile) : null),
    [coverFile]
  );
  const isbnPreview = useMemo(
    () => (isbnFile ? URL.createObjectURL(isbnFile) : null),
    [isbnFile]
  );

  if (!open) return null;

  const resetToMenu = () => {
    setStep('menu');
    setError(null);
    setCandidate(null);
    setSessionId('');
    setDoneMessage(null);
    setCanSearchByPhoto(false);
    setPhotoSearchExhausted(false);
    setManualUrl('');
  };

  const applyStepResult = (
    result: BookLookupStepResult,
    fromPhoto: boolean
  ) => {
    setSessionId(result.sessionId);
    setAttemptsUsed(result.attemptsUsed);
    setAttemptsMax(result.attemptsMax);
    setCanSearchByPhoto(Boolean(result.canSearchByPhoto));
    setPhotoSearchExhausted(Boolean(result.photoSearchExhausted));
    if (fromPhoto) setHasCoverInSession(true);

    if (result.awaitingOcrConfirm) {
      setOcrTitle(result.queryTitle || '');
      setOcrAuthor(result.queryAuthor || '');
      setOcrIsbn(result.queryIsbn || '');
      setCandidate(null);
      setStep('ocrConfirm');
      return;
    }

    if (result.done || !result.candidate) {
      setCandidate(null);
      setDoneMessage(result.message || 'Больш варыянтаў няма.');
      setEditTitle(result.queryTitle || textTitle || ocrTitle || '');
      setEditAuthor(result.queryAuthor || ocrAuthor || '');
      setEditIsbn(result.queryIsbn || textIsbn || ocrIsbn || '');
      setEditPublisher('');
      setStep('exhausted');
      return;
    }
    setCandidate(result.candidate);
    setStep('confirm');
  };

  const startPhotoLookup = async () => {
    if (!coverFile) {
      setError('Дадайце фота вокладкі.');
      return;
    }
    setError(null);
    setStep('busy');
    try {
      const cover = await compressImageForUpload(coverFile, {
        maxEdge: 1600,
        quality: 0.82,
        maxBytes: 480_000,
      });
      const isbnPhoto = isbnFile
        ? await compressImageForUpload(isbnFile, {
            maxEdge: 1400,
            quality: 0.8,
            maxBytes: 650_000,
          })
        : null;
      const result = await lookupBookFromPhoto({
        cover,
        isbnPhoto,
        supplierId,
      });
      applyStepResult(result, true);
    } catch (err) {
      const message = err instanceof Error ? err.message : 'Памылка пошуку';
      setError(
        /413|занадта вялік/i.test(message)
          ? 'Фота з тэлефона занадта вялікае. Паўтарыце спробу — мы сціскаем здымкі аўтаматычна; калі не дапамагае, зрабіце здымак яшчэ раз або захавайце як JPG.'
          : message
      );
      setStep('photo');
    }
  };

  const confirmOcrAndSearch = async () => {
    if (!sessionId) return;
    if (!ocrTitle.trim()) {
      setError('Укажыце назву кнігі.');
      return;
    }
    setError(null);
    setStep('busy');
    try {
      const result = await confirmBookOcrAndSearch({
        sessionId,
        title: ocrTitle.trim(),
        author: ocrAuthor.trim() || undefined,
        isbn: ocrIsbn.trim() || undefined,
      });
      applyStepResult(result, true);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Памылка пошуку');
      setStep('ocrConfirm');
    }
  };

  const startTextLookup = async () => {
    if (!textTitle.trim()) {
      setError('Укажыце назву кнігі.');
      return;
    }
    setError(null);
    setStep('busy');
    setHasCoverInSession(false);
    try {
      const result = await lookupBookFromText({
        title: textTitle.trim(),
        isbn: textIsbn.trim() || undefined,
        supplierId,
      });
      applyStepResult(result, false);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Памылка пошуку');
      setStep('text');
    }
  };

  const searchFurther = async () => {
    if (!sessionId) return;
    setError(null);
    setStep('busy');
    try {
      const result = await lookupBookNext(sessionId);
      applyStepResult(result, hasCoverInSession);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Памылка пошуку');
      setStep('confirm');
    }
  };

  const searchByPhoto = async () => {
    if (!sessionId) return;
    setError(null);
    setStep('busy');
    try {
      const result = await lookupBookByPhoto(sessionId);
      applyStepResult(result, true);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Памылка пошуку па фота');
      setStep('exhausted');
    }
  };

  const importManualUrl = async () => {
    const url = manualUrl.trim();
    if (!url) {
      setError('Увядзіце спасылку на кнігу.');
      return;
    }
    setError(null);
    setStep('busy');
    try {
      const imported = await lookupBookFromUrl({
        sessionId: sessionId || undefined,
        url,
      });
      setEditTitle(imported.title?.trim() || '');
      setEditAuthor(imported.author?.trim() || '');
      setEditIsbn(imported.isbn?.trim() || '');
      setEditPublisher(imported.publisher?.trim() || '');
      setCandidate(imported);
      setStep('edit');
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Памылка чытання спасылкі');
      setStep('exhausted');
    }
  };

  const acceptCandidate = () => {
    if (!candidate) return;
    // Always use shop-page fields from the candidate (not OCR query text).
    setEditTitle(candidate.title?.trim() || '');
    setEditAuthor(candidate.author?.trim() || '');
    setEditIsbn(candidate.isbn?.trim() || '');
    setEditPublisher(candidate.publisher?.trim() || '');
    setStep('edit');
  };

  const createProduct = async () => {
    if (!editTitle.trim()) {
      setError('Укажыце назву.');
      return;
    }
    setError(null);
    setStep('busy');
    try {
      const created = await createBookFromLookup({
        sessionId,
        title: editTitle.trim(),
        author: editAuthor.trim() || undefined,
        isbn: editIsbn.trim() || undefined,
        publisher: editPublisher.trim() || undefined,
        salePrice: Number(salePrice.replace(',', '.')) || 0,
        unitCost: Number(unitCost.replace(',', '.')) || 0,
        useCoverImage: hasCoverInSession,
      });
      onCreated(created);
      onClose();
      resetToMenu();
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Памылка стварэння');
      setStep('edit');
    }
  };

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/40 p-4">
      <div className="max-h-[90vh] w-full max-w-lg overflow-y-auto rounded-2xl bg-white shadow-xl">
        <div className="flex items-center justify-between border-b border-gray-100 px-5 py-4">
          <h2 className="text-base font-semibold text-gray-900">Новы тавар</h2>
          <button
            type="button"
            className="rounded-lg p-1.5 text-gray-500 hover:bg-gray-100"
            onClick={() => {
              onClose();
              resetToMenu();
            }}
            aria-label="Закрыць"
          >
            <FiX className="size-5" />
          </button>
        </div>

        <div className="space-y-4 px-5 py-4">
          {error && (
            <div className="rounded-lg border border-red-200 bg-red-50 px-3 py-2 text-sm text-red-800">
              {error}
            </div>
          )}

          {step === 'menu' && (
            <div className="space-y-2">
              <p className="text-sm text-gray-600">
                Абярыце спосаб стварэння кнігі.
              </p>
              <button
                type="button"
                className="w-full rounded-xl border border-gray-200 px-4 py-3 text-left text-sm font-medium text-gray-900 hover:bg-gray-50"
                onClick={() => {
                  onOpenShopifyManual();
                  onClose();
                }}
              >
                Уручную ў Shopify
              </button>
              <button
                type="button"
                className="w-full rounded-xl border border-gray-200 px-4 py-3 text-left text-sm font-medium text-gray-900 hover:bg-gray-50"
                onClick={() => {
                  setError(null);
                  setStep('photo');
                }}
              >
                Праз ШІ: фота вокладкі (+ ISBN)
              </button>
              <button
                type="button"
                className="w-full rounded-xl border border-gray-200 px-4 py-3 text-left text-sm font-medium text-gray-900 hover:bg-gray-50"
                onClick={() => {
                  setError(null);
                  setStep('text');
                }}
              >
                Праз ШІ: назва і ISBN
              </button>
            </div>
          )}

          {step === 'photo' && (
            <div className="space-y-3">
              <div>
                <label className="mb-1 block text-sm font-medium text-gray-700">
                  Вокладка *
                </label>
                <input
                  type="file"
                  accept="image/*"
                  capture="environment"
                  onChange={(e) => setCoverFile(e.target.files?.[0] ?? null)}
                />
                {coverPreview && (
                  // eslint-disable-next-line @next/next/no-img-element
                  <img
                    src={coverPreview}
                    alt="Вокладка"
                    className="mt-2 max-h-40 rounded-lg border border-gray-200 object-contain"
                  />
                )}
              </div>
              <div>
                <label className="mb-1 block text-sm font-medium text-gray-700">
                  ISBN (фота, па жаданні)
                </label>
                <input
                  type="file"
                  accept="image/*"
                  capture="environment"
                  onChange={(e) => setIsbnFile(e.target.files?.[0] ?? null)}
                />
                {isbnPreview && (
                  // eslint-disable-next-line @next/next/no-img-element
                  <img
                    src={isbnPreview}
                    alt="ISBN"
                    className="mt-2 max-h-28 rounded-lg border border-gray-200 object-contain"
                  />
                )}
                <p className="mt-1 text-xs text-gray-500">
                  Лепш дадаць фота штрыхкода / старонкі з ISBN.
                </p>
              </div>
              <div className="flex gap-2 pt-1">
                <button
                  type="button"
                  className="rounded-lg border border-gray-200 px-4 py-2 text-sm"
                  onClick={resetToMenu}
                >
                  Назад
                </button>
                <button
                  type="button"
                  className="rounded-lg bg-primary px-4 py-2 text-sm font-medium text-white hover:bg-primary-hover disabled:opacity-60"
                  disabled={!coverFile}
                  onClick={() => void startPhotoLookup()}
                >
                  Прачытаць фота
                </button>
              </div>
            </div>
          )}

          {step === 'ocrConfirm' && (
            <div className="space-y-3">
              <p className="text-sm font-medium text-gray-900">
                Ці правільна прачытана з фота?
              </p>
              <p className="text-xs text-gray-500">
                Можаце выправіць палі перад пошукам у інтэрнэце.
              </p>
              <div>
                <label className="mb-1 block text-sm font-medium text-gray-700">
                  Назва кнігі *
                </label>
                <input
                  className={inputClass}
                  value={ocrTitle}
                  onChange={(e) => setOcrTitle(e.target.value)}
                />
              </div>
              <div>
                <label className="mb-1 block text-sm font-medium text-gray-700">
                  Аўтар
                </label>
                <input
                  className={inputClass}
                  value={ocrAuthor}
                  onChange={(e) => setOcrAuthor(e.target.value)}
                />
              </div>
              <div>
                <label className="mb-1 block text-sm font-medium text-gray-700">
                  ISBN
                </label>
                <input
                  className={inputClass}
                  value={ocrIsbn}
                  onChange={(e) => setOcrIsbn(e.target.value)}
                />
              </div>
              <div className="flex flex-wrap gap-2 pt-1">
                <button
                  type="button"
                  className="rounded-lg bg-primary px-4 py-2 text-sm font-medium text-white hover:bg-primary-hover"
                  onClick={() => void confirmOcrAndSearch()}
                >
                  Так, шукаць
                </button>
                <button
                  type="button"
                  className="rounded-lg border border-gray-200 px-4 py-2 text-sm"
                  onClick={() => {
                    setError(null);
                    setStep('photo');
                  }}
                >
                  Назад да фота
                </button>
              </div>
            </div>
          )}

          {step === 'text' && (
            <div className="space-y-3">
              <div>
                <label className="mb-1 block text-sm font-medium text-gray-700">
                  Назва *
                </label>
                <input
                  className={inputClass}
                  value={textTitle}
                  onChange={(e) => setTextTitle(e.target.value)}
                  placeholder="Назва кнігі"
                />
              </div>
              <div>
                <label className="mb-1 block text-sm font-medium text-gray-700">
                  ISBN
                </label>
                <input
                  className={inputClass}
                  value={textIsbn}
                  onChange={(e) => setTextIsbn(e.target.value)}
                  placeholder="978..."
                />
              </div>
              <div className="flex gap-2 pt-1">
                <button
                  type="button"
                  className="rounded-lg border border-gray-200 px-4 py-2 text-sm"
                  onClick={resetToMenu}
                >
                  Назад
                </button>
                <button
                  type="button"
                  className="rounded-lg bg-primary px-4 py-2 text-sm font-medium text-white hover:bg-primary-hover"
                  onClick={() => void startTextLookup()}
                >
                  Шукаць
                </button>
              </div>
            </div>
          )}

          {step === 'busy' && (
            <div className="py-8 text-center text-sm text-gray-600">
              Апрацоўваем…
            </div>
          )}

          {step === 'confirm' && candidate && (
            <div className="space-y-3">
              <p className="text-sm font-medium text-gray-900">
                Ці гэта тая кніга?
              </p>
              <div className="rounded-xl border border-gray-200 bg-gray-50 px-4 py-3 text-sm">
                <div className="font-semibold text-gray-900">
                  {candidate.title}
                </div>
                {candidate.author && (
                  <div className="mt-1 text-gray-600">{candidate.author}</div>
                )}
                {candidate.isbn && (
                  <div className="mt-1 text-gray-600">
                    ISBN: {candidate.isbn}
                  </div>
                )}
                {candidate.publisher && (
                  <div className="mt-1 text-gray-600">
                    {candidate.publisher}
                  </div>
                )}
                {candidate.snippet && (
                  <p className="mt-2 text-xs text-gray-500">
                    {candidate.snippet}
                  </p>
                )}
                <a
                  href={candidate.url}
                  target="_blank"
                  rel="noopener noreferrer"
                  className="mt-3 inline-flex items-center gap-1 break-all text-primary hover:underline"
                >
                  <FiExternalLink className="size-3.5 shrink-0" />
                  {candidate.url}
                </a>
                <div className="mt-2 text-xs text-gray-500">
                  Крыніца: {sourceLabel(candidate.source)} · спроба{' '}
                  {attemptsUsed}/{attemptsMax || '—'}
                </div>
              </div>
              <div className="flex flex-wrap gap-2">
                <button
                  type="button"
                  className="rounded-lg bg-primary px-4 py-2 text-sm font-medium text-white hover:bg-primary-hover"
                  onClick={acceptCandidate}
                >
                  Так, гэта яна
                </button>
                <button
                  type="button"
                  className="rounded-lg border border-gray-200 px-4 py-2 text-sm"
                  onClick={() => void searchFurther()}
                >
                  Не, шукаць далей
                </button>
              </div>
            </div>
          )}

          {(step === 'edit' || step === 'exhausted') && (
            <div className="space-y-3">
              {step === 'exhausted' && (
                <p className="text-sm text-gray-600">
                  {doneMessage || 'Не знойдзена.'} Можаце шукаць па фота, увесці
                  спасылку, запоўніць палі ўручную або адкрыць Shopify.
                </p>
              )}
              {step === 'exhausted' && canSearchByPhoto && (
                <button
                  type="button"
                  className="w-full rounded-lg border border-primary/40 bg-primary/5 px-4 py-2.5 text-sm font-medium text-primary hover:bg-primary/10"
                  onClick={() => void searchByPhoto()}
                >
                  Шукаць па фота вокладкі
                </button>
              )}
              {step === 'exhausted' && (
                <div className="space-y-2 rounded-xl border border-gray-200 bg-gray-50 px-3 py-3">
                  <label className="block text-sm font-medium text-gray-700">
                    Спасылка на кнігу
                  </label>
                  <input
                    className={inputClass}
                    value={manualUrl}
                    onChange={(e) => setManualUrl(e.target.value)}
                    placeholder="https://…"
                  />
                  <button
                    type="button"
                    className="rounded-lg border border-gray-200 bg-white px-4 py-2 text-sm hover:bg-gray-50"
                    onClick={() => void importManualUrl()}
                  >
                    Падцягнуць даныя са спасылкі
                  </button>
                  {photoSearchExhausted && !canSearchByPhoto && (
                    <p className="text-xs text-gray-500">
                      Пошук па фота ўжо выкарыстаны.
                    </p>
                  )}
                </div>
              )}
              <div>
                <label className="mb-1 block text-sm font-medium text-gray-700">
                  Назва *
                </label>
                <input
                  className={inputClass}
                  value={editTitle}
                  onChange={(e) => setEditTitle(e.target.value)}
                />
              </div>
              <div>
                <label className="mb-1 block text-sm font-medium text-gray-700">
                  Аўтар
                </label>
                <input
                  className={inputClass}
                  value={editAuthor}
                  onChange={(e) => setEditAuthor(e.target.value)}
                />
              </div>
              <div>
                <label className="mb-1 block text-sm font-medium text-gray-700">
                  ISBN
                </label>
                <input
                  className={inputClass}
                  value={editIsbn}
                  onChange={(e) => setEditIsbn(e.target.value)}
                />
              </div>
              <div>
                <label className="mb-1 block text-sm font-medium text-gray-700">
                  Выдавец
                </label>
                <input
                  className={inputClass}
                  value={editPublisher}
                  onChange={(e) => setEditPublisher(e.target.value)}
                />
              </div>
              <div className="grid grid-cols-2 gap-3">
                <div>
                  <label className="mb-1 block text-sm font-medium text-gray-700">
                    Цана продажу
                  </label>
                  <input
                    className={inputClass}
                    value={salePrice}
                    onChange={(e) => setSalePrice(e.target.value)}
                    inputMode="decimal"
                  />
                </div>
                <div>
                  <label className="mb-1 block text-sm font-medium text-gray-700">
                    Сабекошт
                  </label>
                  <input
                    className={inputClass}
                    value={unitCost}
                    onChange={(e) => setUnitCost(e.target.value)}
                    inputMode="decimal"
                  />
                </div>
              </div>
              <div className="flex flex-wrap gap-2 pt-1">
                <button
                  type="button"
                  className="rounded-lg bg-primary px-4 py-2 text-sm font-medium text-white hover:bg-primary-hover"
                  onClick={() => void createProduct()}
                >
                  Стварыць у Shopify (draft)
                </button>
                {step === 'exhausted' && (
                  <button
                    type="button"
                    className="rounded-lg border border-gray-200 px-4 py-2 text-sm"
                    onClick={() => {
                      onOpenShopifyManual();
                      onClose();
                    }}
                  >
                    Адкрыць Shopify
                  </button>
                )}
                <button
                  type="button"
                  className="rounded-lg border border-gray-200 px-4 py-2 text-sm"
                  onClick={resetToMenu}
                >
                  Назад
                </button>
              </div>
            </div>
          )}
        </div>
      </div>
    </div>
  );
}
