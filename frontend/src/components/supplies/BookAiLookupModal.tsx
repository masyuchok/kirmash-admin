'use client';

import { useCallback, useEffect, useRef, useState } from 'react';
import { FiX } from 'react-icons/fi';
import {
  attachDraftImages,
  bookCandidateNeedsEnrichment,
  createBookDraftShell,
  ensureRemoteImageInTemp,
  fetchBookGenreOptions,
  fetchBookVendorOptions,
  lookupBookFromUrl,
  lookupSupplierCost,
  mergeBookCandidates,
  styleBookCover,
  suggestBookGenres,
  suggestBookVendor,
  tempMediaAbsoluteUrl,
  type BookLookupCandidate,
  type CachedRemoteImage,
} from '@/lib/api/bookLookup';
import { dataUrlFromImgElement } from '@/lib/imageDataUrl';
import { lookupProductFromShopPageInBrowser } from '@/lib/shop/lookupProductFromShopPageInBrowser';
import { resolveLanguage } from '@/lib/books/bibliographicFields';

type Step = 'menu' | 'link' | 'edit' | 'busy';

type Props = {
  open: boolean;
  supplierId?: string;
  /** Sits above another modal (acceptance) without changing the supplies flow. */
  overlayClassName?: string;
  initialSalePrice?: number | null;
  initialUnitCost?: number | null;
  initialQuantity?: string;
  onClose: () => void;
  onCreated: (product: {
    shopifyProductId: string;
    shopifyVariantId: string;
    title: string;
    quantity: number;
    salePrice: number;
    unitCost: number;
    productAuthor?: string;
    isbn?: string;
    productAdminUrl?: string;
    mainImageUrl?: string | null;
  }) => void;
  onOpenShopifyManual: () => void;
};

const inputClass =
  'w-full rounded-md border border-gray-200 bg-white px-2.5 py-1.5 text-sm text-gray-900 shadow-sm placeholder:text-gray-400 focus-visible:border-primary focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary/25';

const labelClass = 'mb-0.5 block text-xs font-medium text-gray-600';

export default function BookAiLookupModal({
  open,
  supplierId,
  overlayClassName,
  initialSalePrice,
  initialUnitCost,
  initialQuantity,
  onClose,
  onCreated,
  onOpenShopifyManual,
}: Props) {
  const [step, setStep] = useState<Step>('menu');
  const [error, setError] = useState<string | null>(null);
  const [manualUrl, setManualUrl] = useState('');
  const [candidate, setCandidate] = useState<BookLookupCandidate | null>(null);
  const [editTitle, setEditTitle] = useState('');
  const [editAuthor, setEditAuthor] = useState('');
  const [editIsbn, setEditIsbn] = useState('');
  const [editPublisher, setEditPublisher] = useState('');
  const [editDescription, setEditDescription] = useState('');
  const [coverImageUrl, setCoverImageUrl] = useState<string | null>(null);
  const [additionalImageUrls, setAdditionalImageUrls] = useState<string[]>([]);
  /** Data URLs for gallery images when we successfully proxied them. */
  const [additionalImageDataUrls, setAdditionalImageDataUrls] = useState<
    Record<string, string>
  >({});
  /** Server-side temp cache ids (bytes live on our API for ~45 min). */
  /** Final cover id: styled if present, else original fetch. */
  const [coverTempMediaId, setCoverTempMediaId] = useState<string | null>(null);
  const [additionalTempMediaIds, setAdditionalTempMediaIds] = useState<
    Record<string, string>
  >({});
  const [styledCoverPreview, setStyledCoverPreview] = useState<string | null>(
    null
  );
  const [styledCoverLoading, setStyledCoverLoading] = useState(false);
  const additionalImgRefs = useRef<Record<string, HTMLImageElement | null>>({});
  /** Refs keep latest ids even if create started while style was finishing. */
  const sourceCoverTempMediaIdRef = useRef<string | null>(null);
  const styledCoverTempMediaIdRef = useRef<string | null>(null);
  const coverTempMediaIdRef = useRef<string | null>(null);
  const styledCoverPreviewRef = useRef<string | null>(null);
  const [coverLightboxOpen, setCoverLightboxOpen] = useState(false);
  const [lightboxSrc, setLightboxSrc] = useState<string | null>(null);
  const [salePrice, setSalePrice] = useState('0');
  const [unitCost, setUnitCost] = useState('0');
  const [editWeight, setEditWeight] = useState('');
  const [quantity, setQuantity] = useState('1');
  const [editCoverType, setEditCoverType] = useState<'' | 'soft' | 'hard'>('');
  const [editAgeRating, setEditAgeRating] = useState('');
  const [editFormat, setEditFormat] = useState('');
  const [editIllustrator, setEditIllustrator] = useState('');
  const [editLanguage, setEditLanguage] = useState('');
  const [editPageCount, setEditPageCount] = useState('');
  const [editPlace, setEditPlace] = useState('');
  const [editTranslation, setEditTranslation] = useState('');
  const [editYear, setEditYear] = useState('');
  const [genreOptions, setGenreOptions] = useState<string[]>([]);
  const [selectedGenres, setSelectedGenres] = useState<string[]>([]);
  const [genresLoading, setGenresLoading] = useState(false);
  const [genresSuggesting, setGenresSuggesting] = useState(false);
  const [vendorOptions, setVendorOptions] = useState<string[]>([]);
  const [vendorsLoading, setVendorsLoading] = useState(false);
  const [vendorSuggesting, setVendorSuggesting] = useState(false);
  const [priceListRowText, setPriceListRowText] = useState<string | null>(null);

  const [coverCacheError, setCoverCacheError] = useState<string | null>(null);

  const setSourceCoverTemp = (id: string | null) => {
    sourceCoverTempMediaIdRef.current = id;
  };
  const setStyledCoverTemp = (id: string | null) => {
    styledCoverTempMediaIdRef.current = id;
  };
  const setCurrentCoverTemp = (id: string | null) => {
    coverTempMediaIdRef.current = id;
    setCoverTempMediaId(id);
  };
  const setStyledPreview = (src: string | null) => {
    styledCoverPreviewRef.current = src;
    setStyledCoverPreview(src);
  };

  const tempMediaIdFromSrc = (
    src: string | null | undefined
  ): string | null => {
    if (!src) return null;
    const match = src.match(/\/temp-media\/([a-f0-9]+)/i);
    return match?.[1] ?? null;
  };

  const resolveAttachCoverTempMediaId = (): string | null => {
    const sourceTempMediaId = sourceCoverTempMediaIdRef.current;
    const styledTempMediaId =
      styledCoverTempMediaIdRef.current ||
      tempMediaIdFromSrc(styledCoverPreviewRef.current || styledCoverPreview);
    const currentCoverTempMediaId =
      coverTempMediaIdRef.current || coverTempMediaId;
    // Styled wins; never prefer stale fetch/source id after style.
    return styledTempMediaId || currentCoverTempMediaId || sourceTempMediaId;
  };

  // Successful import preview = our temp/styled bytes only. CDN alone is NOT success.
  const coverTempPreviewUrl =
    styledCoverPreview &&
    (styledCoverPreview.startsWith('data:') ||
      styledCoverPreview.includes('/temp-media/'))
      ? styledCoverPreview
      : coverTempMediaId
        ? tempMediaAbsoluteUrl(`/books/temp-media/${coverTempMediaId}`)
        : null;
  const displayCoverSrc = coverTempPreviewUrl;
  const showCdnSourceOnly = Boolean(coverImageUrl) && !coverTempPreviewUrl;
  const weightMissing = !editWeight.trim();
  const weightInputClass = weightMissing
    ? `${inputClass} border-red-400 focus-visible:border-red-500 focus-visible:ring-red-500/25`
    : inputClass;

  const unitCostNum = Number(unitCost.replace(',', '.')) || 0;
  const salePriceNum = Number(salePrice.replace(',', '.')) || 0;
  const marginPercent =
    salePriceNum > 0
      ? Math.round(((salePriceNum - unitCostNum) / salePriceNum) * 100)
      : null;

  const formatPriceInput = (value: number | null | undefined) => {
    if (value == null || !Number.isFinite(value)) return '0';
    return String(value);
  };

  const plainTextToHtml = (text: string) => {
    const trimmed = text.trim();
    if (!trimmed) return '';
    return trimmed
      .split(/\n{2,}/)
      .map((p) => `<p>${p.replace(/\n/g, '<br/>')}</p>`)
      .join('');
  };

  const ensureGenreOptions = useCallback(async () => {
    if (genreOptions.length > 0) return genreOptions;
    setGenresLoading(true);
    try {
      const result = await fetchBookGenreOptions();
      setGenreOptions(result.options);
      return result.options;
    } catch {
      setGenreOptions([]);
      return [] as string[];
    } finally {
      setGenresLoading(false);
    }
  }, [genreOptions]);

  const ensureVendorOptions = useCallback(async () => {
    if (vendorOptions.length > 0) return vendorOptions;
    setVendorsLoading(true);
    try {
      const options = await fetchBookVendorOptions();
      setVendorOptions(options);
      return options;
    } catch {
      setVendorOptions([]);
      return [] as string[];
    } finally {
      setVendorsLoading(false);
    }
  }, [vendorOptions]);

  const loadSupplierUnitCost = useCallback(
    async (opts: {
      title?: string;
      isbn?: string;
      author?: string;
      fillWeightIfMissing?: boolean;
      fillCoverIfMissing?: boolean;
      fillAgeIfMissing?: boolean;
      fillBiblioIfMissing?: boolean;
    }): Promise<string | null> => {
      const sid = supplierId ? Number(supplierId) : NaN;
      if (!Number.isFinite(sid) || sid <= 0) {
        setUnitCost('0');
        setPriceListRowText(null);
        return null;
      }
      try {
        const match = await lookupSupplierCost({
          supplierId: sid,
          title: opts.title,
          isbn: opts.isbn,
          author: opts.author,
        });
        setUnitCost(formatPriceInput(match.unitCostBrutto));
        setPriceListRowText(match.priceListRowText);
        if (
          opts.fillWeightIfMissing &&
          match.weightKg != null &&
          match.weightKg > 0
        ) {
          setEditWeight((prev) =>
            prev.trim() ? prev : String(match.weightKg)
          );
        }
        if (opts.fillCoverIfMissing && match.coverType) {
          setEditCoverType((prev) => prev || match.coverType || '');
        }
        if (opts.fillAgeIfMissing && match.ageRating) {
          setEditAgeRating((prev) => prev.trim() || match.ageRating || '');
        }
        if (opts.fillBiblioIfMissing) {
          if (match.format) {
            setEditFormat((prev) => prev.trim() || match.format || '');
          }
          if (match.illustrator) {
            setEditIllustrator(
              (prev) => prev.trim() || match.illustrator || ''
            );
          }
          if (match.language) {
            setEditLanguage((prev) => prev.trim() || match.language || '');
          }
          if (match.pageCount != null && match.pageCount > 0) {
            setEditPageCount((prev) =>
              prev.trim() ? prev : String(match.pageCount)
            );
          }
          if (match.placeOfPublication) {
            setEditPlace(
              (prev) => prev.trim() || match.placeOfPublication || ''
            );
          }
          if (match.translation) {
            setEditTranslation(
              (prev) => prev.trim() || match.translation || ''
            );
          }
          if (match.year != null && match.year > 0) {
            setEditYear((prev) => (prev.trim() ? prev : String(match.year)));
          }
        }
        return match.priceListRowText;
      } catch {
        setUnitCost('0');
        setPriceListRowText(null);
        return null;
      }
    },
    [supplierId]
  );

  const runVendorSuggest = useCallback(
    async (opts?: {
      publisher?: string | null;
      snippet?: string | null;
      priceListRow?: string | null;
      title?: string | null;
      author?: string | null;
      isbn?: string | null;
    }) => {
      setVendorSuggesting(true);
      try {
        await ensureVendorOptions();
        const matched = await suggestBookVendor({
          publisher: (opts?.publisher ?? editPublisher).trim() || undefined,
          supplierPageSnippet:
            opts?.snippet?.trim() || candidate?.snippet?.trim() || undefined,
          priceListRowText: opts?.priceListRow ?? priceListRowText ?? undefined,
          title: (opts?.title ?? editTitle).trim() || undefined,
          author: (opts?.author ?? editAuthor).trim() || undefined,
          isbn: (opts?.isbn ?? editIsbn).trim() || undefined,
          supplierId: supplierId ? Number(supplierId) : null,
        });
        if (matched) {
          setEditPublisher(matched);
        }
      } catch {
        // Keep page publisher if Shopify match fails.
      } finally {
        setVendorSuggesting(false);
      }
    },
    [
      candidate?.snippet,
      editAuthor,
      editIsbn,
      editPublisher,
      editTitle,
      ensureVendorOptions,
      priceListRowText,
      supplierId,
    ]
  );

  const runGenreSuggest = useCallback(
    async (opts?: {
      title?: string;
      author?: string;
      description?: string;
      coverType?: string;
      publisher?: string;
      isbn?: string;
      snippet?: string | null;
      priceListRow?: string | null;
    }) => {
      const title = (opts?.title ?? editTitle).trim();
      if (!title) return;
      setGenresSuggesting(true);
      try {
        await ensureGenreOptions();
        const suggested = await suggestBookGenres({
          title,
          author: (opts?.author ?? editAuthor).trim() || undefined,
          description:
            (opts?.description ?? editDescription).trim() || undefined,
          coverType: opts?.coverType ?? (editCoverType || null),
          publisher: (opts?.publisher ?? editPublisher).trim() || undefined,
          isbn: (opts?.isbn ?? editIsbn).trim() || undefined,
          supplierPageSnippet:
            opts?.snippet?.trim() || candidate?.snippet?.trim() || undefined,
          priceListRowText: opts?.priceListRow ?? priceListRowText ?? undefined,
          supplierId: supplierId ? Number(supplierId) : null,
        });
        setSelectedGenres(suggested);
      } catch {
        // Keep manual selection if AI suggest fails.
      } finally {
        setGenresSuggesting(false);
      }
    },
    [
      candidate?.snippet,
      editAuthor,
      editCoverType,
      editDescription,
      editIsbn,
      editPublisher,
      editTitle,
      ensureGenreOptions,
      priceListRowText,
      supplierId,
    ]
  );

  const toggleGenre = (label: string) => {
    setSelectedGenres((prev) =>
      prev.includes(label) ? prev.filter((g) => g !== label) : [...prev, label]
    );
  };

  const applyCandidatePrices = useCallback(
    async (cand: BookLookupCandidate | null): Promise<string | null> => {
      setSalePrice(formatPriceInput(cand?.salePrice));
      const hasPageWeight = cand?.weightKg != null && Number(cand.weightKg) > 0;
      const pageCover =
        cand?.coverType === 'soft' || cand?.coverType === 'hard'
          ? cand.coverType
          : '';
      setEditCoverType(pageCover);
      const pageAge = (cand?.ageRating || '').trim();
      setEditAgeRating(pageAge);
      const pageFormat = (cand?.format || '').trim();
      const pageIllustrator = (cand?.illustrator || '').trim();
      const pageLanguage =
        (cand?.language || '').trim() ||
        resolveLanguage(null, cand?.description, cand?.title, cand?.snippet) ||
        '';
      const pagePages =
        cand?.pageCount != null && cand.pageCount > 0
          ? String(cand.pageCount)
          : '';
      const pagePlace = (cand?.placeOfPublication || '').trim();
      const pageTranslation = (cand?.translation || '').trim();
      const pageYear =
        cand?.year != null && cand.year > 0 ? String(cand.year) : '';
      setEditFormat(pageFormat);
      setEditIllustrator(pageIllustrator);
      setEditLanguage(pageLanguage);
      setEditPageCount(pagePages);
      setEditPlace(pagePlace);
      setEditTranslation(pageTranslation);
      setEditYear(pageYear);
      return loadSupplierUnitCost({
        title: cand?.title || undefined,
        isbn: cand?.isbn || undefined,
        author: cand?.author || undefined,
        fillWeightIfMissing: !hasPageWeight,
        fillCoverIfMissing: !pageCover,
        fillAgeIfMissing: !pageAge,
        fillBiblioIfMissing: true,
      });
    },
    [loadSupplierUnitCost]
  );

  const applyCandidateMedia = (cand: BookLookupCandidate | null) => {
    const nextCover = cand?.coverImageUrl?.trim() || null;
    const nextExtra = (cand?.additionalImageUrls ?? [])
      .map((u) => u.trim())
      .filter((u) => /^https?:\/\//i.test(u) && u !== nextCover);
    setCoverImageUrl(nextCover);
    setAdditionalImageUrls(nextExtra);
    setAdditionalImageDataUrls({});
    setCurrentCoverTemp(null);
    setSourceCoverTemp(null);
    setStyledCoverTemp(null);
    setAdditionalTempMediaIds({});
    setCoverCacheError(null);
    setStyledPreview(null);
    if (cand?.weightKg != null && cand.weightKg > 0) {
      setEditWeight(String(cand.weightKg));
    } else {
      setEditWeight('');
    }
  };

  const prefetchAdditionalImages = useCallback(
    async (urls: string[], pageUrl?: string | null) => {
      const idEntries: Record<string, string> = {};
      const dataEntries: Record<string, string> = {};
      const cached = await Promise.all(
        urls.map((url) => ensureRemoteImageInTemp(url, { pageUrl }))
      );
      urls.forEach((url, index) => {
        const item = cached[index];
        if (!item?.found) return;
        if (item.tempMediaId) idEntries[url] = item.tempMediaId;
        if (item.tempMediaPath) {
          dataEntries[url] = tempMediaAbsoluteUrl(item.tempMediaPath);
        } else if (item.dataUrl?.startsWith('data:')) {
          dataEntries[url] = item.dataUrl;
        }
      });
      if (Object.keys(idEntries).length > 0) {
        setAdditionalTempMediaIds((prev) => ({ ...prev, ...idEntries }));
      }
      if (Object.keys(dataEntries).length > 0) {
        setAdditionalImageDataUrls((prev) => ({ ...prev, ...dataEntries }));
      }
    },
    []
  );

  const formatCacheFailure = (cached: CachedRemoteImage | null) => {
    if (cached?.statusCode === 403) {
      return (
        cached.error ||
        'HTTP 403: Cloudflare/сервер пастаўшчыка заблакаваў спампоўку з IP нашага backend.'
      );
    }
    return (
      cached?.error ||
      'fetch-cover не вярнуў tempMediaId (байты не на серверы).'
    );
  };

  const openLightbox = (src: string) => {
    setLightboxSrc(src);
    setCoverLightboxOpen(true);
  };

  const loadStyledCover = useCallback(
    async (opts: {
      coverImageUrl?: string | null;
      pageUrl?: string | null;
    }) => {
      const url = opts.coverImageUrl?.trim() || '';
      const pageUrl = opts.pageUrl?.trim() || null;
      if (!url) {
        setStyledPreview(null);
        setCurrentCoverTemp(null);
        setSourceCoverTemp(null);
        setStyledCoverTemp(null);
        setCoverCacheError(null);
        return;
      }
      setStyledCoverLoading(true);
      setCoverCacheError(null);
      try {
        const cached = await ensureRemoteImageInTemp(url, { pageUrl });
        if (cached?.tempMediaId) {
          setSourceCoverTemp(cached.tempMediaId);
          setStyledCoverTemp(null);
          setCurrentCoverTemp(cached.tempMediaId);
        }
        if (!cached?.tempMediaId) {
          setCoverCacheError(formatCacheFailure(cached));
          setStyledPreview(null);
          return;
        }

        const previewFromCache = cached.tempMediaPath
          ? tempMediaAbsoluteUrl(cached.tempMediaPath)
          : cached.dataUrl || null;

        const applyStyled = (styled: {
          dataUrl: string;
          tempMediaId?: string;
          tempMediaPath?: string;
        }) => {
          if (styled.tempMediaId) {
            setStyledCoverTemp(styled.tempMediaId);
            setCurrentCoverTemp(styled.tempMediaId);
          }
          if (styled.tempMediaPath) {
            setStyledPreview(tempMediaAbsoluteUrl(styled.tempMediaPath));
            return;
          }
          if (styled.dataUrl) {
            setStyledPreview(styled.dataUrl);
            return;
          }
          setStyledPreview(previewFromCache);
        };

        try {
          const styled = await styleBookCover({
            coverImageBase64: cached.dataUrl || undefined,
            coverTempMediaId: cached.tempMediaId || undefined,
          });
          applyStyled(styled);
          return;
        } catch {
          setStyledPreview(previewFromCache);
          return;
        }
      } catch (err) {
        setStyledPreview(null);
        setCoverCacheError(
          err instanceof Error ? err.message : 'Памылка кэшавання вокладкі'
        );
      } finally {
        setStyledCoverLoading(false);
      }
    },
    []
  );

  const salePriceDefault =
    initialSalePrice != null &&
    Number.isFinite(initialSalePrice) &&
    initialSalePrice > 0
      ? String(initialSalePrice)
      : '0';
  const unitCostDefault =
    initialUnitCost != null &&
    Number.isFinite(initialUnitCost) &&
    initialUnitCost > 0
      ? String(initialUnitCost)
      : '0';
  const quantityDefault = initialQuantity ?? '1';

  useEffect(() => {
    if (!open || step !== 'menu') return;
    setSalePrice(salePriceDefault);
    setUnitCost(unitCostDefault);
    setQuantity(quantityDefault);
  }, [open, step, salePriceDefault, unitCostDefault, quantityDefault]);

  if (!open) return null;

  const resetToMenu = () => {
    setStep('menu');
    setError(null);
    setCandidate(null);
    setManualUrl('');
    setCoverImageUrl(null);
    setAdditionalImageUrls([]);
    setAdditionalImageDataUrls({});
    setCurrentCoverTemp(null);
    setSourceCoverTemp(null);
    setStyledCoverTemp(null);
    setAdditionalTempMediaIds({});
    setCoverCacheError(null);
    setStyledPreview(null);
    setStyledCoverLoading(false);
    setCoverLightboxOpen(false);
    setLightboxSrc(null);
    setEditWeight('');
    setSalePrice(salePriceDefault);
    setUnitCost(unitCostDefault);
    setQuantity(quantityDefault);
    setEditCoverType('');
    setEditAgeRating('');
    setEditFormat('');
    setEditIllustrator('');
    setEditLanguage('');
    setEditPageCount('');
    setEditPlace('');
    setEditTranslation('');
    setEditYear('');
    setSelectedGenres([]);
    setGenresSuggesting(false);
    setPriceListRowText(null);
    setEditTitle('');
    setEditAuthor('');
    setEditIsbn('');
    setEditPublisher('');
    setEditDescription('');
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
      const fromPage = await lookupProductFromShopPageInBrowser(url);
      let fromBackend: BookLookupCandidate | null = null;
      if (bookCandidateNeedsEnrichment(fromPage)) {
        try {
          fromBackend = await lookupBookFromUrl({ url });
        } catch (err) {
          if (!fromPage?.title?.trim()) {
            throw err;
          }
        }
      }
      const imported = mergeBookCandidates(fromPage, fromBackend);
      if (!imported?.title?.trim()) {
        throw new Error(
          'Не ўдалося прачытаць назву са старонкі крамы. Паспрабуйце яшчэ раз або ўвядзіце даныя ўручную.'
        );
      }

      const slugNote = imported.snippet?.trim() || '';
      const isSlugGuess =
        !fromPage &&
        (slugNote.includes('Назва з URL') ||
          slugNote.includes('крама не аддала'));

      setEditTitle(imported.title?.trim() || '');
      setEditAuthor(imported.author?.trim() || '');
      setEditIsbn(imported.isbn?.trim() || '');
      setEditPublisher(imported.publisher?.trim() || '');
      setEditDescription(imported.description?.trim() || '');
      applyCandidateMedia(imported);
      setCandidate(imported);
      setError(isSlugGuess ? slugNote : null);
      setSelectedGenres([]);
      setStep('edit');
      // Capture photo bytes ASAP (browser CORS / public proxies) while user fills the form.
      void (async () => {
        const coverUrl = imported.coverImageUrl?.trim() || null;
        const extras = (imported.additionalImageUrls ?? [])
          .map((u) => u.trim())
          .filter((u) => /^https?:\/\//i.test(u));
        const pageUrl = imported.url?.trim() || null;
        await Promise.all([
          loadStyledCover({ coverImageUrl: coverUrl, pageUrl }),
          extras.length > 0
            ? prefetchAdditionalImages(extras, pageUrl)
            : Promise.resolve(),
        ]);
      })();
      void (async () => {
        const priceRow = await applyCandidatePrices(imported);
        await ensureGenreOptions();
        await ensureVendorOptions();
        await Promise.all([
          runGenreSuggest({
            title: imported.title?.trim() || '',
            author: imported.author?.trim() || '',
            description: imported.description?.trim() || '',
            coverType:
              imported.coverType === 'soft' || imported.coverType === 'hard'
                ? imported.coverType
                : undefined,
            publisher: imported.publisher?.trim() || '',
            isbn: imported.isbn?.trim() || '',
            snippet: imported.snippet,
            priceListRow: priceRow,
          }),
          runVendorSuggest({
            publisher: imported.publisher?.trim() || '',
            snippet: imported.snippet,
            priceListRow: priceRow,
            title: imported.title?.trim() || '',
            author: imported.author?.trim() || '',
            isbn: imported.isbn?.trim() || '',
          }),
        ]);
      })();
    } catch (err) {
      const message =
        err instanceof Error ? err.message : 'Памылка чытання спасылкі';
      setError(`${message} Можаце запоўніць назву і аўтара ўручную ніжэй.`);
      setEditTitle('');
      setEditAuthor('');
      setEditIsbn('');
      setEditPublisher('');
      setEditDescription('');
      setCoverImageUrl(null);
      setAdditionalImageUrls([]);
      setAdditionalImageDataUrls({});
      setCurrentCoverTemp(null);
      setSourceCoverTemp(null);
      setStyledCoverTemp(null);
      setAdditionalTempMediaIds({});
      setStyledPreview(null);
      setEditWeight('');
      setSalePrice('0');
      setUnitCost('0');
      setSelectedGenres([]);
      setCandidate({
        title: '',
        url,
        source: 'manual',
      });
      setStep('edit');
      void ensureGenreOptions();
      void ensureVendorOptions();
    }
  };

  const createProduct = async () => {
    if (!editTitle.trim()) {
      setError('Укажыце назву.');
      return;
    }
    if (styledCoverLoading) {
      setError('Пачакайце: вокладка яшчэ апрацоўваецца.');
      return;
    }
    setError(null);
    setStep('busy');
    // Open synchronously in the click gesture — after await browsers block popups.
    const adminTab =
      typeof window !== 'undefined'
        ? window.open('about:blank', '_blank')
        : null;
    try {
      const unitCostAtCreate = Number(unitCost.replace(',', '.'));
      const saleAtCreate = Number(salePrice.replace(',', '.'));
      const quantityAtCreate = Number(quantity.replace(',', '.'));
      const descriptionHtml = plainTextToHtml(editDescription);
      const saleParsed = saleAtCreate;

      const previewSrc =
        styledCoverPreviewRef.current || styledCoverPreview || null;
      const coverDataUrlAtStart = previewSrc?.startsWith('data:')
        ? previewSrc.trim()
        : '';
      const coverTempIdAtStart = resolveAttachCoverTempMediaId();
      const hasStyledCoverAtStart =
        Boolean(styledCoverTempMediaIdRef.current) ||
        Boolean(tempMediaIdFromSrc(previewSrc)) ||
        Boolean(previewSrc?.includes('/temp-media/'));
      const pageUrlAtStart = candidate?.url;
      const extraUrlsAtStart = additionalImageUrls;
      const extraTempIdsAtStart = additionalTempMediaIds;
      const extraDataUrlsAtStart = additionalImageDataUrls;
      const expectCover = Boolean(
        coverImageUrl || coverTempIdAtStart || coverDataUrlAtStart
      );
      const seoImageIds = [
        ...(expectCover ? ['cover'] : []),
        ...extraUrlsAtStart.map((_, index) => `extra-${index + 1}`),
      ];

      // Image bytes are not needed to create the Shopify draft. Prepare them
      // in parallel and upload after the product id exists.
      const imagesPromise = (async () => {
        let coverDataUrl = coverDataUrlAtStart;
        let coverTempId = coverTempIdAtStart;

        if (!coverTempId && !coverDataUrl && coverImageUrl) {
          const cached = await ensureRemoteImageInTemp(coverImageUrl, {
            pageUrl: pageUrlAtStart,
          });
          if (cached?.tempMediaId) {
            coverTempId = cached.tempMediaId;
          }
          if (cached?.dataUrl?.startsWith('data:') && !coverDataUrl) {
            coverDataUrl = cached.dataUrl;
          }
        }

        if (coverTempId && !hasStyledCoverAtStart && !coverDataUrl) {
          try {
            const styled = await styleBookCover({
              coverTempMediaId: coverTempId,
              coverImageBase64: coverDataUrl || undefined,
            });
            if (styled.tempMediaId) coverTempId = styled.tempMediaId;
            else if (styled.dataUrl) coverDataUrl = styled.dataUrl;
          } catch {
            // keep raw temp bytes
          }
        }

        const extras = await Promise.all(
          extraUrlsAtStart.map(async (url, index) => {
            const seoId = `extra-${index + 1}`;
            const tempId = extraTempIdsAtStart[url];
            if (!tempId) {
              const ensured = await ensureRemoteImageInTemp(url, {
                pageUrl: pageUrlAtStart,
              });
              if (ensured?.tempMediaId) {
                return { seoId, tempId: ensured.tempMediaId, dataUrl: '' };
              }
              if (ensured?.dataUrl?.startsWith('data:')) {
                return { seoId, tempId: '', dataUrl: ensured.dataUrl };
              }
            }
            if (tempId) return { seoId, tempId, dataUrl: '' };
            const cached = extraDataUrlsAtStart[url];
            if (cached?.startsWith('data:')) {
              return { seoId, tempId: '', dataUrl: cached };
            }
            const fromImg = dataUrlFromImgElement(
              additionalImgRefs.current[url]
            );
            if (fromImg?.startsWith('data:')) {
              return { seoId, tempId: '', dataUrl: fromImg };
            }
            return { seoId, tempId: '', dataUrl: '' };
          })
        );

        const readyExtras = extras.filter(
          (item) => item.tempId || item.dataUrl.startsWith('data:')
        );
        const hasCoverBytes =
          Boolean(coverTempId) || coverDataUrl.startsWith('data:');
        return {
          coverTempId,
          coverDataUrl,
          hasCoverBytes,
          readyExtras,
          hasAnyPhoto: hasCoverBytes || readyExtras.length > 0,
        };
      })();

      // Shell without media — photos via attach-draft-images when available.
      const weightParsed = Number(editWeight.replace(',', '.'));
      const quantityParsed = quantityAtCreate;
      const pageCountParsed = Number(editPageCount.replace(',', '.'));
      const yearParsed = Number(editYear.replace(',', '.'));
      const created = await createBookDraftShell({
        title: editTitle.trim(),
        descriptionHtml: descriptionHtml || undefined,
        salePrice:
          Number.isFinite(saleParsed) && saleParsed > 0 ? saleParsed : 0,
        useCoverImage: false,
        isbn: editIsbn.trim() || undefined,
        weightKg:
          Number.isFinite(weightParsed) && weightParsed > 0
            ? weightParsed
            : undefined,
        quantity:
          Number.isFinite(quantityParsed) && quantityParsed > 0
            ? Math.floor(quantityParsed)
            : undefined,
        author: editAuthor.trim() || undefined,
        coverType: editCoverType || undefined,
        ageRating: editAgeRating.trim() || undefined,
        format: editFormat.trim() || undefined,
        illustrator: editIllustrator.trim() || undefined,
        language: editLanguage.trim() || undefined,
        pageCount:
          Number.isFinite(pageCountParsed) && pageCountParsed > 0
            ? Math.floor(pageCountParsed)
            : undefined,
        placeOfPublication: editPlace.trim() || undefined,
        translation: editTranslation.trim() || undefined,
        year:
          Number.isFinite(yearParsed) && yearParsed > 0
            ? Math.floor(yearParsed)
            : undefined,
        publisher: editPublisher.trim() || undefined,
        genres: selectedGenres,
        seoImageIds,
      });
      if (!created.shopifyProductId) {
        adminTab?.close();
        throw new Error('Няма Shopify product id.');
      }

      const shopifyProductId = created.shopifyProductId;
      const imageAlts = created.imageAlts;
      void imagesPromise
        .then(async (images) => {
          if (!images.hasAnyPhoto) return;
          const coverAlt =
            imageAlts.find((alt) => alt.imageId === 'cover')?.alt || undefined;
          const additionalDataUrls = images.readyExtras
            .map((item) => item.dataUrl)
            .filter((url) => url.startsWith('data:'));
          const additionalTempMediaIds = images.readyExtras
            .map((item) => item.tempId)
            .filter((id) => id.length > 0);
          const additionalImageAlts = images.readyExtras.map(
            (item) =>
              imageAlts.find((alt) => alt.imageId === item.seoId)?.alt || ''
          );
          const media = await attachDraftImages({
            shopifyProductId,
            coverDataUrl: images.coverDataUrl.startsWith('data:')
              ? images.coverDataUrl
              : null,
            coverTempMediaId: images.hasCoverBytes ? images.coverTempId : null,
            coverImageUrl: null,
            additionalDataUrls,
            additionalImageUrls: [],
            additionalTempMediaIds,
            coverImageAlt: coverAlt,
            additionalImageAlts,
          });
          if (media.errors.length > 0) {
            console.warn('attach-draft-images partial errors', media.errors);
          }
        })
        .catch((mediaErr: unknown) => {
          console.warn('attach-draft-images failed', mediaErr);
        });

      if (!created.shopifyAdminUrl) {
        adminTab?.close();
        throw new Error('Няма спасылкі на картку ў Shopify.');
      }
      if (adminTab && !adminTab.closed) {
        adminTab.location.href = created.shopifyAdminUrl;
      } else {
        window.open(created.shopifyAdminUrl, '_blank', 'noopener,noreferrer');
      }

      const unitCostParsed = unitCostAtCreate;
      onCreated({
        shopifyProductId: created.shopifyProductId,
        shopifyVariantId: created.shopifyVariantId || '',
        title: created.title || editTitle.trim(),
        quantity:
          Number.isFinite(quantityParsed) && quantityParsed > 0
            ? Math.floor(quantityParsed)
            : 1,
        salePrice:
          Number.isFinite(saleParsed) && saleParsed > 0 ? saleParsed : 0,
        unitCost:
          Number.isFinite(unitCostParsed) && unitCostParsed > 0
            ? unitCostParsed
            : 0,
        productAuthor: editAuthor.trim() || undefined,
        isbn: editIsbn.trim() || undefined,
        productAdminUrl: created.shopifyAdminUrl,
        mainImageUrl: coverImageUrl || styledCoverPreview || null,
      });

      onClose();
      resetToMenu();
    } catch (err) {
      adminTab?.close();
      setError(err instanceof Error ? err.message : 'Памылка стварэння');
      setStep('edit');
    }
  };

  const removeCoverPhoto = () => {
    setCoverImageUrl(null);
    setStyledPreview(null);
    setCurrentCoverTemp(null);
    setSourceCoverTemp(null);
    setStyledCoverTemp(null);
    setCoverCacheError(null);
  };

  const removeAdditionalPhoto = (url: string) => {
    setAdditionalImageUrls((prev) => prev.filter((u) => u !== url));
    setAdditionalImageDataUrls((prev) => {
      const next = { ...prev };
      delete next[url];
      return next;
    });
    setAdditionalTempMediaIds((prev) => {
      const next = { ...prev };
      delete next[url];
      return next;
    });
  };

  const coverThumb = () => {
    const hasGallery = additionalImageUrls.length > 0;
    if (
      !displayCoverSrc &&
      !showCdnSourceOnly &&
      !styledCoverLoading &&
      !hasGallery &&
      !coverCacheError
    ) {
      return null;
    }
    return (
      <div className="shrink-0">
        <label className={labelClass}>Фота</label>
        {coverCacheError ? (
          <p className="mb-1 text-xs text-red-600">{coverCacheError}</p>
        ) : null}
        <div className="flex flex-wrap items-start gap-2">
          {styledCoverLoading && !displayCoverSrc ? (
            <div className="flex h-28 w-28 items-center justify-center rounded-lg border border-gray-200 bg-gray-50 text-xs text-gray-500">
              Апрацоўка…
            </div>
          ) : displayCoverSrc ? (
            <div className="relative shrink-0">
              <button
                type="button"
                className="group relative block overflow-hidden rounded-lg border border-gray-200 bg-neutral-950 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary/40"
                onClick={() => openLightbox(displayCoverSrc)}
                title="Павялічыць"
              >
                {/* eslint-disable-next-line @next/next/no-img-element */}
                <img
                  src={displayCoverSrc}
                  alt="Вокладка"
                  className="h-28 w-28 object-contain transition group-hover:opacity-90"
                />
              </button>
              <button
                type="button"
                className="absolute -right-1.5 -top-1.5 z-10 flex size-5 items-center justify-center rounded-full border border-gray-200 bg-white text-gray-600 shadow-sm hover:bg-red-50 hover:text-red-600"
                onClick={removeCoverPhoto}
                aria-label="Прыбраць вокладку"
                title="Прыбраць"
              >
                <FiX className="size-3" />
              </button>
            </div>
          ) : showCdnSourceOnly && coverImageUrl ? (
            <div className="relative shrink-0 overflow-hidden rounded-lg border border-amber-300 bg-amber-50">
              {/* eslint-disable-next-line @next/next/no-img-element */}
              <img
                src={coverImageUrl}
                alt="Вокладка (часова з CDN)"
                className="h-28 w-28 object-contain opacity-60"
              />
              <div className="bg-amber-600 px-1 py-0.5 text-center text-[10px] text-white">
                Чакаем кэш…
              </div>
            </div>
          ) : coverCacheError ? (
            <div className="flex h-28 w-28 items-center justify-center rounded-lg border border-red-200 bg-red-50 p-2 text-center text-[10px] text-red-700">
              Не ўдалося загрузіць
            </div>
          ) : null}
          {additionalImageUrls.map((url) => (
            <div key={url} className="relative shrink-0">
              <button
                type="button"
                className="group relative block overflow-hidden rounded-lg border border-gray-200 bg-gray-50 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary/40"
                onClick={() =>
                  openLightbox(additionalImageDataUrls[url] || url)
                }
                title="Павялічыць"
              >
                {/* eslint-disable-next-line @next/next/no-img-element */}
                <img
                  ref={(el) => {
                    additionalImgRefs.current[url] = el;
                  }}
                  src={additionalImageDataUrls[url] || url}
                  alt="Дадатковае фота"
                  className="h-28 w-20 object-cover transition group-hover:opacity-90"
                />
              </button>
              <button
                type="button"
                className="absolute -right-1.5 -top-1.5 z-10 flex size-5 items-center justify-center rounded-full border border-gray-200 bg-white text-gray-600 shadow-sm hover:bg-red-50 hover:text-red-600"
                onClick={() => removeAdditionalPhoto(url)}
                aria-label="Прыбраць фота"
                title="Прыбраць"
              >
                <FiX className="size-3" />
              </button>
            </div>
          ))}
        </div>
        {styledCoverLoading && displayCoverSrc ? (
          <p className="mt-1 text-[11px] text-gray-500">
            Апрацоўваем вокладку…
          </p>
        ) : null}
      </div>
    );
  };

  const isWideStep = step === 'edit';

  return (
    <div
      className={
        overlayClassName ??
        'fixed inset-0 z-50 flex items-center justify-center bg-black/40 p-3 sm:p-4'
      }
    >
      <div
        className={`max-h-[92vh] w-full overflow-hidden rounded-2xl bg-white shadow-xl ${
          isWideStep ? 'max-w-4xl' : 'max-w-md'
        }`}
      >
        <div className="flex items-center justify-between border-b border-gray-100 px-4 py-3 sm:px-5">
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

        <div className="max-h-[calc(92vh-3.25rem)] space-y-3 overflow-y-auto px-4 py-3 sm:px-5">
          {error && (
            <div className="rounded-lg border border-red-200 bg-red-50 px-3 py-2 text-sm text-red-800">
              {error}
            </div>
          )}

          {step === 'menu' && (
            <div className="space-y-2">
              <p className="text-sm text-gray-600">
                Абярыце спосаб дадавання кнігі.
              </p>
              <button
                type="button"
                className="w-full rounded-xl border border-gray-200 px-4 py-3 text-left text-sm font-medium text-gray-900 hover:bg-gray-50"
                onClick={() => {
                  setError(null);
                  setManualUrl('');
                  setStep('link');
                }}
              >
                Спасылка на прадукт з сайта пастаўшчыка
              </button>
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
            </div>
          )}

          {step === 'link' && (
            <div className="space-y-3">
              <p className="text-sm text-gray-600">
                Устаўце спасылку на старонку кнігі ў краме пастаўшчыка —
                падцягнем назву і аўтара, калі старонка даступная.
              </p>
              <div>
                <label className={labelClass}>Спасылка на прадукт *</label>
                <input
                  className={inputClass}
                  value={manualUrl}
                  onChange={(e) => setManualUrl(e.target.value)}
                  placeholder="https://…"
                  autoFocus
                />
              </div>
              <div className="flex flex-wrap gap-2 pt-1">
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
                  disabled={!manualUrl.trim()}
                  onClick={() => void importManualUrl()}
                >
                  Падцягнуць даныя
                </button>
              </div>
              <p className="text-xs text-gray-500">
                Калі аўтаматычнае чытанне не спрацуе (напрыклад, Cloudflare),
                можна запоўніць палі ўручную на наступным кроку.
              </p>
            </div>
          )}

          {step === 'busy' && (
            <div className="py-8 text-center text-sm text-gray-600">
              Апрацоўваем…
            </div>
          )}

          {step === 'edit' && (
            <div className="space-y-3">
              <div className="flex flex-col gap-3 sm:flex-row sm:items-start">
                {coverThumb()}
                <div className="min-w-0 flex-1 space-y-2.5">
                  <div>
                    <label className={labelClass}>Назва *</label>
                    <input
                      className={inputClass}
                      value={editTitle}
                      onChange={(e) => setEditTitle(e.target.value)}
                    />
                  </div>
                  <div className="grid grid-cols-1 gap-2 sm:grid-cols-2">
                    <div className="min-w-0">
                      <label className={labelClass}>Аўтар / аўтары</label>
                      <input
                        className={inputClass}
                        value={editAuthor}
                        onChange={(e) => setEditAuthor(e.target.value)}
                        placeholder="Імя Прозвішча"
                      />
                    </div>
                    <div className="min-w-0">
                      <label className={labelClass}>
                        Выдавец
                        {vendorsLoading || vendorSuggesting ? (
                          <span className="ml-1.5 font-normal text-gray-400">
                            {vendorsLoading ? 'загрузка…' : 'падбор…'}
                          </span>
                        ) : null}
                      </label>
                      <input
                        className={inputClass}
                        list="book-vendor-options"
                        value={editPublisher}
                        onChange={(e) => setEditPublisher(e.target.value)}
                        onFocus={() => void ensureVendorOptions()}
                        placeholder={
                          vendorOptions.length > 0
                            ? 'Shopify Vendor…'
                            : undefined
                        }
                      />
                      <datalist id="book-vendor-options">
                        {vendorOptions.map((label) => (
                          <option key={label} value={label} />
                        ))}
                      </datalist>
                    </div>
                  </div>
                  <div>
                    <label className={labelClass}>Апісанне</label>
                    <textarea
                      className={`${inputClass} min-h-[72px] resize-y`}
                      value={editDescription}
                      onChange={(e) => setEditDescription(e.target.value)}
                      rows={3}
                      placeholder="Апісанне з старонкі крамы…"
                    />
                  </div>
                </div>
              </div>

              <div className="rounded-xl border border-gray-100 bg-gray-50/80 px-3 py-2.5">
                <div className="flex flex-wrap items-end gap-2">
                  <div className="min-w-[6.5rem] flex-1 basis-[6.5rem]">
                    <label className={labelClass}>Цана паст. (брута)</label>
                    <input
                      className={inputClass}
                      value={unitCost}
                      onChange={(e) => setUnitCost(e.target.value)}
                      inputMode="decimal"
                    />
                  </div>
                  <div className="w-[3.75rem] shrink-0">
                    <span className={labelClass}>Маржа</span>
                    <span
                      className={`flex h-[34px] items-center justify-center rounded-md border border-gray-200 bg-white text-sm ${
                        marginPercent == null
                          ? 'text-gray-400'
                          : marginPercent < 0
                            ? 'font-medium text-red-600'
                            : 'font-medium text-gray-900'
                      }`}
                    >
                      {marginPercent == null ? '—' : `${marginPercent}%`}
                    </span>
                  </div>
                  <div className="min-w-[6.5rem] flex-1 basis-[6.5rem]">
                    <label className={labelClass}>Цана продажу</label>
                    <input
                      className={inputClass}
                      value={salePrice}
                      onChange={(e) => setSalePrice(e.target.value)}
                      inputMode="decimal"
                    />
                  </div>
                  <div className="w-14 shrink-0">
                    <label className={labelClass}>Кольк.</label>
                    <input
                      className={inputClass}
                      value={quantity}
                      onChange={(e) => setQuantity(e.target.value)}
                      inputMode="numeric"
                    />
                  </div>
                  <div className="min-w-[8rem] flex-[1.4] basis-[8rem]">
                    <label className={labelClass}>ISBN</label>
                    <input
                      className={inputClass}
                      value={editIsbn}
                      onChange={(e) => setEditIsbn(e.target.value)}
                    />
                  </div>
                  <div className="w-[4.5rem] shrink-0">
                    <label className={labelClass}>Вага (кг)</label>
                    <input
                      className={weightInputClass}
                      value={editWeight}
                      onChange={(e) => setEditWeight(e.target.value)}
                      inputMode="decimal"
                      placeholder="0.35"
                    />
                  </div>
                </div>
                {weightMissing ? (
                  <p className="mt-1.5 text-[11px] text-red-600">
                    Вага не знойдзена — увядзіце ўручную.
                  </p>
                ) : null}
              </div>

              <div className="grid grid-cols-2 gap-2 sm:grid-cols-3 lg:grid-cols-4">
                <div className="min-w-0">
                  <label className={labelClass}>Вокладка</label>
                  <select
                    className={inputClass}
                    value={editCoverType}
                    onChange={(e) =>
                      setEditCoverType(
                        e.target.value === 'soft' || e.target.value === 'hard'
                          ? e.target.value
                          : ''
                      )
                    }
                  >
                    <option value="">— невядома —</option>
                    <option value="soft">Мяккая</option>
                    <option value="hard">Цвёрдая</option>
                  </select>
                </div>
                <div className="min-w-0">
                  <label className={labelClass}>Узрост</label>
                  <input
                    className={inputClass}
                    value={editAgeRating}
                    onChange={(e) => setEditAgeRating(e.target.value)}
                    placeholder="0+ / 18+"
                  />
                </div>
                <div className="min-w-0">
                  <label className={labelClass}>Фармат</label>
                  <input
                    className={inputClass}
                    value={editFormat}
                    onChange={(e) => setEditFormat(e.target.value)}
                    placeholder="130×200"
                  />
                </div>
                <div className="min-w-0">
                  <label className={labelClass}>Старонак</label>
                  <input
                    className={inputClass}
                    value={editPageCount}
                    onChange={(e) => setEditPageCount(e.target.value)}
                    inputMode="numeric"
                  />
                </div>
                <div className="min-w-0">
                  <label className={labelClass}>Год</label>
                  <input
                    className={inputClass}
                    value={editYear}
                    onChange={(e) => setEditYear(e.target.value)}
                    inputMode="numeric"
                    placeholder="2024"
                  />
                </div>
                <div className="min-w-0">
                  <label className={labelClass}>Мова</label>
                  <input
                    className={inputClass}
                    value={editLanguage}
                    onChange={(e) => setEditLanguage(e.target.value)}
                    placeholder="беларуская"
                  />
                </div>
                <div className="min-w-0">
                  <label className={labelClass}>Месца выдання</label>
                  <input
                    className={inputClass}
                    value={editPlace}
                    onChange={(e) => setEditPlace(e.target.value)}
                  />
                </div>
                <div className="min-w-0">
                  <label className={labelClass}>Ілюстратар</label>
                  <input
                    className={inputClass}
                    value={editIllustrator}
                    onChange={(e) => setEditIllustrator(e.target.value)}
                  />
                </div>
                <div className="min-w-0 sm:col-span-2 lg:col-span-2">
                  <label className={labelClass}>Пераклад</label>
                  <input
                    className={inputClass}
                    value={editTranslation}
                    onChange={(e) => setEditTranslation(e.target.value)}
                    placeholder="з польскай"
                  />
                </div>
              </div>

              <div>
                <div className="mb-1 flex flex-wrap items-center justify-between gap-2">
                  <label className="text-xs font-medium text-gray-600">
                    Жанр / жанры
                  </label>
                  <button
                    type="button"
                    className="text-xs font-medium text-primary hover:underline disabled:opacity-50"
                    disabled={
                      genresSuggesting || genresLoading || !editTitle.trim()
                    }
                    onClick={() => void runGenreSuggest()}
                  >
                    {genresSuggesting ? 'Думаем…' : 'Перавызначыць жанры'}
                  </button>
                </div>
                {genresLoading && genreOptions.length === 0 ? (
                  <p className="text-[11px] text-gray-500">
                    Загружаем спіс жанраў…
                  </p>
                ) : genreOptions.length === 0 ? (
                  <p className="text-[11px] text-gray-500">
                    У Shopify няма даступных значэнняў для book.genre.
                  </p>
                ) : (
                  <div className="max-h-36 overflow-y-auto rounded-lg border border-gray-200 bg-white px-2.5 py-2">
                    <div className="grid grid-cols-1 gap-x-3 gap-y-1 sm:grid-cols-2 lg:grid-cols-3">
                      {genreOptions.map((label) => {
                        const checked = selectedGenres.includes(label);
                        return (
                          <label
                            key={label}
                            className="flex cursor-pointer items-start gap-1.5 text-sm text-gray-800"
                          >
                            <input
                              type="checkbox"
                              className="mt-0.5"
                              checked={checked}
                              onChange={() => toggleGenre(label)}
                            />
                            <span className="leading-snug">{label}</span>
                          </label>
                        );
                      })}
                    </div>
                  </div>
                )}
              </div>

              <div className="sticky bottom-0 -mx-4 flex flex-wrap gap-2 border-t border-gray-100 bg-white/95 px-4 py-2.5 backdrop-blur-sm sm:-mx-5 sm:px-5">
                <button
                  type="button"
                  className="rounded-lg bg-primary px-4 py-2 text-sm font-medium text-white hover:bg-primary-hover disabled:opacity-60"
                  onClick={() => void createProduct()}
                  disabled={styledCoverLoading}
                >
                  Стварыць
                </button>
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

      {coverLightboxOpen && (lightboxSrc || displayCoverSrc) ? (
        <div
          className="fixed inset-0 z-[60] flex items-center justify-center bg-black/80 p-4"
          role="dialog"
          aria-modal="true"
          aria-label="Вокладка"
          onClick={() => {
            setCoverLightboxOpen(false);
            setLightboxSrc(null);
          }}
        >
          <button
            type="button"
            className="absolute right-4 top-4 rounded-lg bg-white/10 p-2 text-white hover:bg-white/20"
            onClick={() => {
              setCoverLightboxOpen(false);
              setLightboxSrc(null);
            }}
            aria-label="Закрыць"
          >
            <FiX className="size-5" />
          </button>
          {/* eslint-disable-next-line @next/next/no-img-element */}
          <img
            src={lightboxSrc || displayCoverSrc || ''}
            alt="Фота — павялічаны выгляд"
            className="max-h-[90vh] max-w-[min(90vw,1080px)] object-contain"
            onClick={(e) => e.stopPropagation()}
          />
        </div>
      ) : null}
    </div>
  );
}
