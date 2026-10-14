import i18n from 'i18next';
import { initReactI18next } from 'react-i18next';
import dayjs from 'dayjs';
import 'dayjs/locale/ar';
import 'dayjs/locale/en';
import en from './en.json';
import ar from './ar.json';

export type Lang = 'en' | 'ar';
const LANG_KEY = 'aml.lang';

const initial = ((): Lang => {
  try {
    return localStorage.getItem(LANG_KEY) === 'ar' ? 'ar' : 'en';
  } catch {
    return 'en';
  }
})();

/** Missing translation keys are collected so they can be detected during development/testing. */
export const missingKeys = new Set<string>();

i18n.use(initReactI18next).init({
  resources: { en: { translation: en }, ar: { translation: ar } },
  lng: initial,
  fallbackLng: false,
  interpolation: { escapeValue: false },
  returnNull: false,
  saveMissing: true,
  missingKeyHandler: (lngs, _ns, key) => {
    missingKeys.add(`${lngs.join(',')}:${key}`);
  },
});

export const applyDocumentLang = (lng: Lang) => {
  document.documentElement.lang = lng;
  document.documentElement.dir = lng === 'ar' ? 'rtl' : 'ltr';
  dayjs.locale(lng);
};

applyDocumentLang(initial);

export const setLanguage = async (lng: Lang) => {
  try {
    localStorage.setItem(LANG_KEY, lng);
  } catch {
    /* ignore */
  }
  await i18n.changeLanguage(lng);
  applyDocumentLang(lng);
};

(window as unknown as { __amlMissingKeys: Set<string> }).__amlMissingKeys = missingKeys;

export default i18n;
