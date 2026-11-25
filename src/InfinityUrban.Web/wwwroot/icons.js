const paths = {
  bed:'<path d="M3 18v3m18-3v3M3 18h18v-7H3zm1-7V5h16v6M7 11V8h4v3m2 0V8h4v3"/>',
  bath:'<path d="M3 12h18m-17 0v4a4 4 0 0 0 4 4h8a4 4 0 0 0 4-4v-4M6 20v2m12-2v2M6 12V5a3 3 0 0 1 6 0v1m-2 0h4"/>',
  bot:'<path d="M9 3h6m-3 0v4M5 9h14v10H5zM2 12v4m20-4v4M9 13v2m6-2v2M9 22v-3m6 3v-3"/>',
  grid:'<rect x="3" y="3" width="7" height="7" rx="1"/><rect x="14" y="3" width="7" height="7" rx="1"/><rect x="3" y="14" width="7" height="7" rx="1"/><rect x="14" y="14" width="7" height="7" rx="1"/>',
  home:'<path d="m3 10 9-7 9 7v10a1 1 0 0 1-1 1h-5v-8H9v8H4a1 1 0 0 1-1-1z"/>',
  users:'<path d="M16 21v-2a4 4 0 0 0-4-4H7a4 4 0 0 0-4 4v2m18 0v-2a4 4 0 0 0-3-3.87M16 3a4 4 0 0 1 0 8"/><circle cx="9.5" cy="7" r="4"/>',
  calendar:'<rect x="3" y="5" width="18" height="16" rx="2"/><path d="M16 3v4M8 3v4M3 11h18"/>',
  file:'<path d="M14 2H5v20h14V7zm0 0v6h5M8 12h8m-8 4h8"/>',
  repeat:'<path d="m17 2 4 4-4 4M3 11V8a2 2 0 0 1 2-2h16M7 22l-4-4 4-4m14-1v3a2 2 0 0 1-2 2H3"/>',
  briefcase:'<rect x="3" y="7" width="18" height="14" rx="2"/><path d="M8 7V3h8v4m-8 0v14m8-14v14"/>',
  message:'<path d="M21 15a2 2 0 0 1-2 2H7l-5 4V5a2 2 0 0 1 2-2h15a2 2 0 0 1 2 2z"/>',
  clipboard:'<rect x="5" y="4" width="14" height="18" rx="2"/><rect x="9" y="2" width="6" height="4" rx="1"/><path d="m9 14 2 2 4-4"/>',
  link:'<path d="M10 7H7a5 5 0 0 0 0 10h3m4-10h3a5 5 0 0 1 0 10h-3M8 12h8"/>',
  shield:'<path d="m12 2 8 4v6c0 5-8 10-8 10S4 17 4 12V6z"/><path d="m9 12 2 2 4-4"/>',
  panel:'<rect x="3" y="3" width="18" height="18" rx="2"/><path d="M9 3v18"/>',
  languages:'<path d="M3 5h12M9 2v3m4 0c-1 6-4 9-9 11m1-8c1 3 4 6 8 8m1 5 4-10 4 10m-6-4h4"/>',
  sun:'<circle cx="12" cy="12" r="4"/><path d="M12 2v2m0 16v2M2 12h2m16 0h2M5 5l1.5 1.5m11 11L19 19M5 19l1.5-1.5m11-11L19 5"/>',
  moon:'<path d="M20.9 13A9 9 0 0 1 11 3.1 9 9 0 1 0 20.9 13Z"/>',
  bell:'<path d="M18 8a6 6 0 0 0-12 0c0 7-3 7-3 9h18c0-2-3-2-3-9M10 21h4"/>',
  logout:'<path d="M9 21H3V3h6m7 4 5 5-5 5m5-5H9"/>',
  close:'<path d="m6 6 12 12M6 18 18 6"/>'
};
export function icon(name) { return `<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.65" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">${paths[name] || paths.grid}</svg>`; }
export function hydrateIcons(root = document) { for (const node of root.querySelectorAll('[data-icon]')) node.insertAdjacentHTML('afterbegin', icon(node.dataset.icon)); }
