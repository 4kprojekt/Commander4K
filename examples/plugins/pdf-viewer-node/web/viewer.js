// PDF viewer page. Runs in the app's WebView2 preview, talks to the app only through window.c4k
// (docs/plugins.md, "Web previews"). PDF.js comes from ./pdfjs (vendored, see pdfjs/SOURCE.md).
import * as pdfjsLib from './pdfjs/build/pdf.min.mjs';

// pdf_viewer.mjs reads globalThis.pdfjsLib, which pdf.min.mjs sets; import it only after that.
const { EventBus, PDFLinkService, PDFFindController, PDFViewer } = await import('./pdfjs/web/pdf_viewer.mjs');

const c4k = window.c4k;
const asset = path => new URL(path, import.meta.url).href;

pdfjsLib.GlobalWorkerOptions.workerSrc = asset('./pdfjs/build/pdf.worker.min.mjs');

const $ = id => document.getElementById(id);
const container = $('viewerContainer');
const pageNumber = $('pageNumber');
const pageCount = $('pageCount');
const zoomSelect = $('zoomSelect');
const customZoom = $('customZoom');
const findInput = $('findInput');
const findCount = $('findCount');
const status = $('status');
const printButton = $('print');
const printContainer = $('printContainer');

const eventBus = new EventBus();
const linkService = new PDFLinkService({ eventBus });
const findController = new PDFFindController({ eventBus, linkService });
const viewer = new PDFViewer({
  container,
  eventBus,
  linkService,
  findController,
  imageResourcesPath: asset('./pdfjs/web/images/'),
});
linkService.setViewer(viewer);

let zoom = c4k?.state?.zoom ?? 'page-width'; // the user's zoom, kept across files
let loadingTask = null;
let pdfDocument = null;
let generation = 0;
let printing = false;

// ---- skin ----

function applySkin(skin) {
  if (!skin) return;
  const style = document.documentElement.style;
  const colours = { bg: skin.background, fg: skin.foreground, accent: skin.accent, muted: skin.muted, selection: skin.selection, border: skin.border };
  for (const [name, value] of Object.entries(colours)) if (value) style.setProperty(`--${name}`, value);
  if (skin.fontFamily) style.setProperty('--font', `"${skin.fontFamily}", "Segoe UI", sans-serif`);
  if (skin.fontSize) style.setProperty('--font-size', `${skin.fontSize}px`);
}

// ---- loading ----

async function load(url) {
  const current = ++generation;
  const previous = loadingTask;
  loadingTask = null;
  pdfDocument = null;
  viewer.setDocument(null);
  linkService.setDocument(null);
  clearPrint();
  findCount.textContent = '';
  pageCount.textContent = '/ –';
  pageNumber.value = 1;
  setStatus('Wczytywanie…');
  if (previous) await previous.destroy().catch(() => {});
  if (current !== generation) return;

  const task = pdfjsLib.getDocument({
    url,
    rangeChunkSize: 65536,
    disableAutoFetch: true,
    disableStream: false,
    cMapUrl: asset('./pdfjs/cmaps/'),
    cMapPacked: true,
    standardFontDataUrl: asset('./pdfjs/standard_fonts/'),
    wasmUrl: asset('./pdfjs/wasm/'),
    iccUrl: asset('./pdfjs/iccs/'),
    isEvalSupported: false,
  });
  loadingTask = task;
  let passwordCancelled = false;
  task.onPassword = (update, reason) => {
    const password = window.prompt(reason === pdfjsLib.PasswordResponses.INCORRECT_PASSWORD
      ? 'Nieprawidłowe hasło. Podaj hasło do pliku PDF:'
      : 'Plik PDF jest chroniony hasłem. Podaj hasło:');
    if (password === null) {
      passwordCancelled = true;
      task.destroy();
    } else update(password);
  };
  try {
    const pdf = await task.promise;
    if (current !== generation) return;
    pdfDocument = pdf;
    viewer.setDocument(pdf);
    linkService.setDocument(pdf, null);
    pageCount.textContent = `/ ${pdf.numPages}`;
    pageNumber.max = pdf.numPages;
    setStatus('');
  } catch (error) {
    if (current !== generation) return;
    const message = passwordCancelled || error?.name === 'PasswordException' ? 'Anulowano: plik chroniony hasłem' : `Nie można otworzyć pliku PDF: ${error?.message ?? error}`;
    setStatus(message, true);
  }
}

function updatePageButtons(number) {
  $('previous').disabled = number <= 1;
  $('next').disabled = number >= viewer.pagesCount;
}

eventBus.on('pagesinit', () => {
  viewer.currentScaleValue = String(zoom);
  updatePageButtons(viewer.currentPageNumber);
  container.focus({ preventScroll: true });
  if (findInput.value) find('');
});

eventBus.on('pagechanging', ({ pageNumber: number }) => {
  pageNumber.value = number;
  updatePageButtons(number);
});

eventBus.on('scalechanging', ({ scale, presetValue }) => {
  const preset = presetValue ?? String(scale);
  const option = [...zoomSelect.options].find(o => o.value === preset && o !== customZoom);
  if (option) {
    customZoom.hidden = true;
    zoomSelect.value = option.value;
  } else {
    customZoom.hidden = false;
    customZoom.textContent = `${Math.round(scale * 100)}%`;
    zoomSelect.value = 'custom';
  }
});

function setStatus(text, error = false) {
  status.textContent = text;
  status.style.color = error ? '#e5484d' : '';
}

// ---- toolbar ----

$('previous').addEventListener('click', () => viewer.previousPage());
$('next').addEventListener('click', () => viewer.nextPage());

pageNumber.addEventListener('keydown', event => {
  if (event.key !== 'Enter') return;
  const number = Number(pageNumber.value);
  if (pdfDocument && Number.isInteger(number) && number >= 1 && number <= pdfDocument.numPages) viewer.currentPageNumber = number;
  else pageNumber.value = viewer.currentPageNumber;
  container.focus({ preventScroll: true });
});

function saveZoom(value) {
  zoom = value;
  c4k?.request('saveZoom', { zoom: value }).catch(error => c4k.notify(`Nie zapisano powiększenia: ${error.message}`, true));
}

$('zoomIn').addEventListener('click', () => {
  if (!pdfDocument) return;
  viewer.increaseScale();
  saveZoom(viewer.currentScale);
});

$('zoomOut').addEventListener('click', () => {
  if (!pdfDocument) return;
  viewer.decreaseScale();
  saveZoom(viewer.currentScale);
});

zoomSelect.addEventListener('change', () => {
  if (zoomSelect.value === 'custom') return;
  const value = Number.isNaN(Number(zoomSelect.value)) ? zoomSelect.value : Number(zoomSelect.value);
  if (pdfDocument) viewer.currentScaleValue = String(value);
  saveZoom(value);
});

// ---- search ----

function find(type, previous = false) {
  eventBus.dispatch('find', {
    source: window,
    type,
    query: findInput.value,
    caseSensitive: false,
    entireWord: false,
    highlightAll: true,
    findPrevious: previous,
    matchDiacritics: false,
  });
}

function showMatches({ matchesCount }) {
  if (!findInput.value) {
    findCount.textContent = '';
    return;
  }
  const { current, total } = matchesCount ?? { current: 0, total: 0 };
  findCount.textContent = total > 0 ? `${current} / ${total}` : '0 / 0';
  findCount.classList.toggle('none', total === 0);
}

eventBus.on('updatefindmatchescount', showMatches);
eventBus.on('updatefindcontrolstate', showMatches);

findInput.addEventListener('input', () => find(''));
findInput.addEventListener('keydown', event => {
  if (event.key !== 'Enter') return;
  event.preventDefault();
  find('again', event.shiftKey);
});
$('findNext').addEventListener('click', () => find('again', false));
$('findPrevious').addEventListener('click', () => find('again', true));

window.addEventListener('keydown', event => {
  if (event.ctrlKey && !event.altKey && !event.shiftKey && event.key.toLowerCase() === 'f') {
    event.preventDefault();
    findInput.focus();
    findInput.select();
  }
});

// ---- printing: every page as a 150 dpi image, shown only by @media print ----

const PRINT_SCALE = 150 / 72;

function clearPrint() {
  for (const image of printContainer.querySelectorAll('img')) URL.revokeObjectURL(image.src);
  printContainer.replaceChildren();
}

async function renderPageImage(pdf, number) {
  const page = await pdf.getPage(number);
  try {
    const viewport = page.getViewport({ scale: PRINT_SCALE });
    const canvas = document.createElement('canvas');
    canvas.width = Math.floor(viewport.width);
    canvas.height = Math.floor(viewport.height);
    const context = canvas.getContext('2d');
    context.fillStyle = '#fff';
    context.fillRect(0, 0, canvas.width, canvas.height);
    await page.render({ canvas, canvasContext: context, viewport, intent: 'print', annotationMode: pdfjsLib.AnnotationMode.ENABLE_STORAGE }).promise;
    const blob = await new Promise(resolve => canvas.toBlob(resolve, 'image/png'));
    canvas.width = canvas.height = 0; // free the bitmap now
    const image = document.createElement('img');
    image.src = URL.createObjectURL(blob);
    image.alt = `Strona ${number}`;
    await image.decode().catch(() => {});
    return image;
  } finally {
    page.cleanup();
  }
}

printButton.addEventListener('click', async () => {
  if (!pdfDocument || printing) return;
  const pdf = pdfDocument;
  const current = generation;
  printing = true;
  printButton.disabled = true;
  clearPrint();
  try {
    for (let number = 1; number <= pdf.numPages; number++) {
      setStatus(`Przygotowanie wydruku: ${number} / ${pdf.numPages}`);
      const image = await renderPageImage(pdf, number);
      if (current !== generation) return;
      printContainer.append(image);
    }
    setStatus('');
    window.print();
  } catch (error) {
    if (current === generation) setStatus(`Nie można wydrukować: ${error?.message ?? error}`, true);
    clearPrint();
  } finally {
    printing = false;
    printButton.disabled = false;
  }
});

window.addEventListener('afterprint', clearPrint);

// ---- the app ----

if (c4k) {
  applySkin(c4k.skin);
  c4k.on('skin', applySkin);
  c4k.on('entry', ({ fileUrl }) => load(fileUrl));
  load(c4k.fileUrl);
} else {
  $('main').innerHTML = '<p class="message">Ta strona działa tylko w podglądzie Commander4K.</p>';
}
