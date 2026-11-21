/**
 * svacitra - Icon system
 * Replaces the emoji that the markup uses as icons with one consistent set of stroke SVG icons.
 * Runs once at load and again for anything added later (folder list, buttons that reset their label).
 * Loaded before the other modules; `PicasaIcons.html(name)` is available to them.
 */

if (window.__PICASA_NATIVE__) document.documentElement.classList.add('native-shell');

window.PicasaIcons = (() => {
  // 24x24, stroke-based. Paths are simple geometric drawings made for this project.
  const P = {
    folder: '<path d="M3 7a2 2 0 0 1 2-2h4l2 2.5h8a2 2 0 0 1 2 2V17a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2z"/>',
    images: '<rect x="3" y="4" width="18" height="16" rx="2.5"/><circle cx="8.5" cy="9.5" r="1.6"/><path d="m4 17 5-5 4 4 3-3 4 4"/>',
    star: '<path d="m12 3.5 2.6 5.4 5.9.8-4.3 4.1 1 5.8-5.2-2.8-5.2 2.8 1-5.8-4.3-4.1 5.9-.8z"/>',
    sparkles: '<path d="M10 3.5 11.8 9l5.2 1.8-5.2 1.8L10 18l-1.8-5.4L3 10.800 8.200 9z"/><path d="M18.500 3v4M16.500 5h4M18.500 16v4M16.500 18h4"/>',
    clock: '<circle cx="12" cy="12" r="8.5"/><path d="M12 7.500V12l3 2"/>',
    search: '<circle cx="11" cy="11" r="6.5"/><path d="m16 16 4.500 4.500"/>',
    play: '<path d="M8 5.500v13l10.500-6.500z"/>',
    pause: '<path d="M8.500 5.500v13M15.500 5.500v13"/>',
    refresh: '<path d="M20 11.500a8 8 0 0 0-14.300-4.200L4 9"/><path d="M4 4.500V9h4.500"/><path d="M4 12.500a8 8 0 0 0 14.300 4.200L20 15"/><path d="M20 19.500V15h-4.500"/>',
    power: '<path d="M12 3.500v8"/><path d="M6.800 7.200a7.500 7.500 0 1 0 10.400 0"/>',
    wand: '<path d="m4 20 11-11"/><path d="m14 7 3 3"/><path d="M18.500 3v3M17 4.500h3M6 4v2.500M4.800 5.200h2.400M19.500 13v2.500M18.200 14.200h2.600"/>',
    sunrise: '<path d="M3 18h18M7 18a5 5 0 0 1 10 0"/><path d="M12 6.500v3M5 11.500l1.800 1.800M19 11.500l-1.800 1.800M5.500 21h13"/>',
    film: '<rect x="3.500" y="4" width="17" height="16" rx="2"/><path d="M8 4v16M16 4v16M3.500 9H8M3.500 15H8M16 9h4.500M16 15h4.500"/>',
    contrast: '<circle cx="12" cy="12" r="8.500"/><path d="M12 3.500v17"/><path d="M12 7.500h6.800M12 12h8.500M12 16.500h6.800"/>',
    sun: '<circle cx="12" cy="12" r="4"/><path d="M12 3v2.500M12 18.500V21M3 12h2.500M18.500 12H21M5.600 5.600l1.800 1.800M16.600 16.600l1.800 1.800M5.600 18.400l1.800-1.800M16.600 7.400l1.800-1.800"/>',
    clapper: '<rect x="3.500" y="10" width="17" height="10" rx="2"/><path d="m3.800 10 15.400-4.200-.900-2.600L3 7.400z"/><path d="m8 6 2.200 2.800M13 4.700l2.200 2.800"/>',
    wrench: '<path d="M14.500 6.500a4.200 4.200 0 0 0 5.300 5.300l-2.300-.5-1.600-1.600-.500-2.300a4.200 4.200 0 0 0-.900-.900z" transform="translate(-1 -1)"/><path d="m13 11-8.500 8.500a1.800 1.800 0 0 0 2.500 2.500L15.500 13.500" transform="translate(-1 -2)"/>',
    sliders: '<path d="M5 4v6M5 14v6M12 4v2M12 10v10M19 4v9M19 17v3"/><circle cx="5" cy="12" r="2"/><circle cx="12" cy="8" r="2"/><circle cx="19" cy="15" r="2"/>',
    rotateLeft: '<path d="M4 5v5h5"/><path d="M4.600 10A8 8 0 1 1 6 16.500"/>',
    rotateRight: '<path d="M20 5v5h-5"/><path d="M19.400 10A8 8 0 1 0 18 16.500"/>',
    eye: '<path d="M2.500 12S6 5.500 12 5.500 21.500 12 21.500 12 18 18.500 12 18.500 2.500 12 2.500 12z"/><circle cx="12" cy="12" r="2.800"/>',
    upload: '<path d="M12 15.500V4M7.500 8 12 3.500 16.500 8"/><path d="M4.500 14.500v3.500a2 2 0 0 0 2 2h11a2 2 0 0 0 2-2v-3.500"/>',
    x: '<path d="m6 6 12 12M18 6 6 18"/>',
    chevronLeft: '<path d="m14.500 5.500-6.500 6.500 6.500 6.500"/>',
    chevronRight: '<path d="m9.500 5.500 6.500 6.500-6.500 6.500"/>',
    grid: '<rect x="4" y="4" width="6.500" height="6.500" rx="1.200"/><rect x="13.500" y="4" width="6.500" height="6.500" rx="1.200"/><rect x="4" y="13.500" width="6.500" height="6.500" rx="1.200"/><rect x="13.500" y="13.500" width="6.500" height="6.500" rx="1.200"/>',
    camera: '<path d="M4 8.500a2 2 0 0 1 2-2h2l1.500-2h5l1.500 2h2a2 2 0 0 1 2 2V17a2 2 0 0 1-2 2H6a2 2 0 0 1-2-2z"/><circle cx="12" cy="12.500" r="3.500"/>',
    loader: '<path d="M12 3.500v3M12 17.500v3M3.500 12h3M17.500 12h3M6 6l2.100 2.100M15.900 15.900 18 18M6 18l2.100-2.100M15.900 8.100 18 6"/>',
    pencil: '<path d="M4 20l1-4.500L16 4.500a2.100 2.100 0 0 1 3 3L8 18.500z"/><path d="m14.500 6 3 3"/>',
    info: '<circle cx="12" cy="12" r="8.500"/><path d="M12 11v5.500M12 7.500v.500"/>',
    crop: '<path d="M7 3v14h14"/><path d="M3 7h14v14"/>',
    ruler: '<path d="m3.500 16.500 13-13 4 4-13 13z"/><path d="m7.500 12.500 1.800 1.800M10.500 9.500l1.800 1.800M13.500 6.500l1.800 1.800"/>',
    scale: '<path d="M12 4v16M7 20h10M5 7h14"/><path d="m5 7-2.500 6a2.700 2.700 0 0 0 5 0zM19 7l-2.500 6a2.700 2.700 0 0 0 5 0z"/>',
    palette: '<path d="M12 3.500a8.500 8.500 0 1 0 0 17c1.400 0 2-1 2-2 0-1.500-1-1.800-1-3 0-1 .800-1.500 2-1.500h2a3.500 3.500 0 0 0 3.500-3.500c0-3.900-3.800-7-8.500-7z"/><circle cx="8" cy="11" r=".900"/><circle cx="10.500" cy="7.500" r=".900"/><circle cx="14.500" cy="7.500" r=".900"/>',
    plus: '<path d="M12 5v14M5 12h14"/>',
    collage: '<rect x="3.500" y="5" width="11" height="9" rx="1.500" transform="rotate(-8 9 9.500)"/><rect x="10" y="10" width="10.500" height="9" rx="1.500" transform="rotate(7 15 14.500)"/>',
    help: '<circle cx="12" cy="12" r="8.500"/><path d="M9.500 9.500a2.500 2.500 0 1 1 3.500 2.300c-.700.400-1 .900-1 1.700M12 16.500v.300"/>',
    keyboard: '<rect x="3" y="6.500" width="18" height="11" rx="2"/><path d="M7 10h.010M10.500 10h.010M14 10h.010M17 10h.010M8 14h8"/>'
  };

  // Emoji (as used in index.html and the JS modules) -> icon name. Unknown emoji are left alone.
  const EMOJI = {
    '\u{1F4C1}': 'folder', '\u{1F5BC}': 'images', '⭐': 'star', '\u{1F916}': 'sparkles', '✨': 'sparkles',
    '\u{1F552}': 'clock', '\u{1F50D}': 'search', '▶': 'play', '↻': 'refresh', '⏻': 'power',
    '\u{1FA84}': 'wand', '\u{1F305}': 'sunrise', '\u{1F39E}': 'film', '\u{1F5A4}': 'contrast', '☀': 'sun',
    '\u{1F3AC}': 'clapper', '\u{1F527}': 'wrench', '\u{1F39A}': 'sliders', '⟲': 'rotateLeft', '⟳': 'rotateRight',
    '\u{1F441}': 'eye', '\u{1F4E4}': 'upload', '✕': 'x', '❮': 'chevronLeft', '❯': 'chevronRight',
    '❚❚': 'pause', '▦': 'grid', '\u{1F4F7}': 'camera', '⏳': 'loader', '✏': 'pencil',
    'ℹ': 'info', '✂': 'crop', '\u{1F4D0}': 'ruler', '⚖': 'scale', '\u{1F3A8}': 'palette',
    '⌨': 'keyboard', '❓': 'help', '\u{1F4E5}': 'upload', '\u{1F4C2}': 'folder', '\u{1F6AA}': 'power'
  };

  const escapeRe = s => s.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
  const keys = Object.keys(EMOJI).sort((a, b) => b.length - a.length);
  const RE = new RegExp('(' + keys.map(escapeRe).join('|') + ')\\uFE0F?', 'gu');
  const SKIP = new Set(['SCRIPT', 'STYLE', 'INPUT', 'TEXTAREA', 'svg', 'OPTION', 'TITLE']);

  function html(name, extraClass) {
    const body = P[name];
    if (!body) return '';
    return `<svg class="pr-icon pr-icon-${name}${extraClass ? ' ' + extraClass : ''}" viewBox="0 0 24 24" width="16" height="16" fill="none" stroke="currentColor" stroke-width="1.750" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">${body}</svg>`;
  }

  function replaceInTextNode(node) {
    const text = node.nodeValue;
    RE.lastIndex = 0;
    if (!RE.test(text)) return;
    RE.lastIndex = 0;
    const frag = document.createDocumentFragment();
    let last = 0, m;
    while ((m = RE.exec(text)) !== null) {
      if (m.index > last) frag.appendChild(document.createTextNode(text.slice(last, m.index)));
      const holder = document.createElement('span');
      holder.className = 'pr-icon-wrap';
      holder.innerHTML = html(EMOJI[m[1]]);
      frag.appendChild(holder);
      last = m.index + m[0].length;
    }
    if (last < text.length) frag.appendChild(document.createTextNode(text.slice(last)));
    node.parentNode.replaceChild(frag, node);
  }

  function decorate(root) {
    if (!root) return;
    if (root.nodeType === Node.TEXT_NODE) {
      if (root.parentNode && !SKIP.has(root.parentNode.nodeName)) replaceInTextNode(root);
      return;
    }
    if (root.nodeType !== Node.ELEMENT_NODE || SKIP.has(root.nodeName)) return;
    const walker = document.createTreeWalker(root, NodeFilter.SHOW_TEXT, {
      acceptNode: n => (n.parentNode && SKIP.has(n.parentNode.nodeName)) ? NodeFilter.FILTER_REJECT : NodeFilter.FILTER_ACCEPT
    });
    const nodes = [];
    while (walker.nextNode()) nodes.push(walker.currentNode);
    nodes.forEach(replaceInTextNode);
  }

  function start() {
    decorate(document.body);
    const gallery = document.getElementById('gallery-content');
    new MutationObserver(records => {
      for (const rec of records) {
        // Photo cards render their own SVG badges; skipping the gallery keeps huge libraries fast.
        if (gallery && gallery.contains(rec.target) && rec.target.id !== 'gallery-content') continue;
        if (rec.type === 'characterData') {
          decorate(rec.target);
          continue;
        }
        rec.addedNodes.forEach(n => {
          if (n.nodeType === Node.ELEMENT_NODE && n.classList && (n.classList.contains('folder-group') || n.classList.contains('pr-icon-wrap'))) return;
          decorate(n);
        });
      }
    }).observe(document.body, { childList: true, subtree: true, characterData: true });
  }

  document.addEventListener('DOMContentLoaded', start);
  return { html, decorate };
})();
