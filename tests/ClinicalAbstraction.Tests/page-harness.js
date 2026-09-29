'use strict';
// Runs the review page's own script against a running server, with a small stand-in for the
// browser's document. The stand-in has no way to turn text into markup, so anything the page
// shows got there as a text node. The tests in PageTests.cs start a server and call this file.
//
//   node page-harness.js <address> visit <hash> [narrow]
//   node page-harness.js <address> crawl <hash> <most pages>
//   node page-harness.js <address> steps <JSON list of {"go": hash} or {"click": link text}>
//
// It prints one JSON object: what the page showed, and any failure it met.

const crypto = require('crypto');
const vm = require('vm');

const [, , address, mode, ...rest] = process.argv;
const narrow = rest.includes('narrow');

// ---------- a document with just enough of the browser's behaviour ----------

const created = new Set();
const consoleErrors = [];
const scrolled = [];
const focused = [];
const requests = [];
let pending = 0;
let hash = '';
const windowListeners = {};
const documentListeners = {};

class BaseNode {
  constructor() { this.parentNode = null; this.childNodes = []; }
  get firstChild() { return this.childNodes[0] || null; }
  get isConnected() { let n = this; while (n.parentNode) n = n.parentNode; return n === root; }
  append(...items) { for (const item of items) this.insert(typeof item === 'string' ? new TextNode(item) : item); }
  insert(child) {
    if (!(child instanceof BaseNode)) throw new TypeError('Only nodes can be added.');
    if (child instanceof Fragment) { for (const c of [...child.childNodes]) this.insert(c); return; }
    if (child.parentNode) child.parentNode.detach(child);
    child.parentNode = this;
    this.childNodes.push(child);
  }
  detach(child) { const i = this.childNodes.indexOf(child); if (i >= 0) this.childNodes.splice(i, 1); child.parentNode = null; }
  replaceChildren(...items) { for (const c of [...this.childNodes]) this.detach(c); this.append(...items); }
  get textContent() { return this.childNodes.map(c => c.textContent).join(''); }
}

class TextNode extends BaseNode {
  constructor(data) { super(); this.data = String(data); }
  get textContent() { return this.data; }
}

class Fragment extends BaseNode {}

class ElementNode extends BaseNode {
  constructor(tag) {
    super();
    this.tagName = tag.toUpperCase();
    this.attributes = new Map();
    this.listeners = {};
    this.className = '';
    this.scrollTop = 0;
    this.open = false;
  }
  setAttribute(name, value) { this.attributes.set(name, String(value)); }
  getAttribute(name) { return this.attributes.has(name) ? this.attributes.get(name) : null; }
  hasAttribute(name) { return this.attributes.has(name); }
  removeAttribute(name) { this.attributes.delete(name); }
  get id() { return this.getAttribute('id') || ''; }
  get classList() {
    const node = this;
    const names = () => new Set(node.className.split(/\s+/).filter(Boolean));
    return {
      toggle(name, force) {
        const set = names();
        const on = force === undefined ? !set.has(name) : Boolean(force);
        if (on) set.add(name); else set.delete(name);
        node.className = [...set].join(' ');
        return on;
      },
      contains(name) { return names().has(name); },
    };
  }
  addEventListener(type, handler) { (this.listeners[type] ||= []).push(handler); }
  dispatch(type, event) { for (const handler of this.listeners[type] || []) handler.call(this, Object.assign({ type, target: this }, event)); }
  focus() { focused.push(this.id || this.tagName.toLowerCase()); }
  scrollIntoView() { scrolled.push(this.id || this.tagName.toLowerCase()); }
  closest(selector) {
    // The page asks only for links that open a step of the trace.
    if (selector !== 'a[data-step]') throw new Error('Unsupported selector ' + selector);
    for (let n = this; n instanceof ElementNode; n = n.parentNode) if (n.tagName === 'A' && n.hasAttribute('data-step')) return n;
    return null;
  }
}

class InputNode extends ElementNode {
  get value() { return this.typed !== undefined ? this.typed : this.getAttribute('value') || ''; }
  set value(text) { this.typed = String(text); }
}

class FormNode extends ElementNode {
  requestSubmit() { this.dispatch('submit', { preventDefault() {} }); }
}

const root = new ElementNode('html');
const body = new ElementNode('body');
root.append(body);
function withId(tag, id) { const n = new ElementNode(tag); n.setAttribute('id', id); return n; }
const nav = withId('ul', 'nav');
const main = withId('main', 'main');
const footer = withId('footer', 'status');
body.append(nav, main, footer);

function all(node, test, out = []) {
  if (test(node)) out.push(node);
  for (const c of node.childNodes) all(c, test, out);
  return out;
}

const document = {
  title: '',
  createElement(tag) {
    created.add(tag.toLowerCase());
    return tag === 'input' || tag === 'textarea' || tag === 'select' ? new InputNode(tag) : tag === 'form' ? new FormNode(tag) : new ElementNode(tag);
  },
  createTextNode(text) { return new TextNode(text); },
  createDocumentFragment() { return new Fragment(); },
  getElementById(id) { return all(root, n => n instanceof ElementNode && n.id === id)[0] || null; },
  addEventListener(type, handler) { (documentListeners[type] ||= []).push(handler); },
};

function fireHashChange() {
  setTimeout(() => { for (const handler of windowListeners.hashchange || []) handler({ type: 'hashchange' }); }, 0);
}

const location = {
  get hash() { return hash; },
  set hash(value) {
    let text = String(value);
    if (!text.startsWith('#')) text = '#' + text;
    if (text === hash) return;
    hash = text;
    fireHashChange();
  },
  replace(url) {
    const at = String(url).indexOf('#');
    const text = at < 0 ? '' : String(url).slice(at);
    if (text === hash) return;
    hash = text;
    fireHashChange();
  },
};

const history = {
  replaceState(state, title, url) { const at = String(url).indexOf('#'); if (at >= 0) hash = String(url).slice(at); },
};

const realFetch = globalThis.fetch;
function pageFetch(url, options) {
  pending++;
  requests.push(String(url));
  return realFetch(new URL(url, address), options).then(response => {
    const read = response.json.bind(response);
    response.json = () => read().finally(() => { pending--; });
    return response;
  }, error => { pending--; throw error; });
}

Object.assign(globalThis, {
  window: globalThis,
  document,
  location,
  history,
  fetch: pageFetch,
  Node: BaseNode,
  Element: ElementNode,
  HTMLInputElement: InputNode,
  matchMedia: query => ({ media: query, matches: !narrow }),
  addEventListener: (type, handler) => { (windowListeners[type] ||= []).push(handler); },
});
globalThis.console = Object.assign(Object.create(console), {
  error: (...args) => consoleErrors.push(args.map(String).join(' ')),
  log: () => {},
});

// ---------- driving the page ----------

async function settle() {
  let quiet = 0;
  for (let i = 0; i < 2000 && quiet < 4; i++) {
    await new Promise(resolve => setTimeout(resolve, 5));
    quiet = pending === 0 ? quiet + 1 : 0;
  }
  if (quiet < 4) throw new Error('The page did not finish loading.');
}

function textOf(node) {
  return all(node, n => n instanceof TextNode).map(n => n.data).join(' | ');
}

function snapshot() {
  const notices = all(main, n => n instanceof ElementNode && /\bnotice\b/.test(n.className) && /\berror\b/.test(n.className));
  return {
    hash,
    title: document.title,
    errors: notices.map(n => textOf(n)),
    text: textOf(main),
    headings: all(main, n => n instanceof ElementNode && /^H[1-4]$/.test(n.tagName)).map(n => n.textContent),
    links: all(main, n => n instanceof ElementNode && n.tagName === 'A').map(a => ({ href: a.getAttribute('href'), text: a.textContent, step: a.getAttribute('data-step') })),
    inputs: all(main, n => n instanceof ElementNode && n.tagName === 'INPUT').map(i => i.id),
    blocks: all(main, n => n instanceof ElementNode && /\bdoc-lines\b/.test(n.className)).length,
    citedLines: all(main, n => n instanceof ElementNode && /\bcited\b/.test(n.className)).map(n => textOf(n)),
  };
}

function click(link) {
  const event = { type: 'click', target: link, defaultPrevented: false, preventDefault() { this.defaultPrevented = true; } };
  for (const handler of documentListeners.click || []) handler(event);
  if (!event.defaultPrevented) location.hash = link.getAttribute('href');
}

async function open(start) {
  const response = await realFetch(new URL('/', address));
  const html = await response.text();
  const policy = response.headers.get('content-security-policy') || '';
  const script = /<script\b[^>]*>([\s\S]*?)<\/script>/.exec(html);
  if (!script) throw new Error('The page has no script.');
  const digest = "'sha256-" + crypto.createHash('sha256').update(script[1], 'utf8').digest('base64') + "'";
  if (!policy.includes(digest)) throw new Error('The security policy does not allow the page script, so a browser would show a blank page.');
  hash = start;
  vm.runInThisContext(script[1], { filename: 'page.html' });
  await settle();
}

function report(extra) {
  process.stdout.write(JSON.stringify(Object.assign({
    created: [...created].sort(),
    consoleErrors,
    scrolled,
    focused,
    requests,
  }, extra)));
}

async function run() {
  if (mode === 'visit') {
    await open(rest[0]);
    report({ pages: [snapshot()] });
  } else if (mode === 'crawl') {
    const most = Number(rest[1] || 400);
    await open(rest[0]);
    const pages = [snapshot()];
    const seen = new Set([hash]);
    const queue = pages[0].links.map(l => l.href).filter(h => h && h.startsWith('#/'));
    while (queue.length && pages.length < most) {
      const target = queue.shift();
      if (seen.has(target)) continue;
      seen.add(target);
      const link = all(main, n => n instanceof ElementNode && n.tagName === 'A' && n.getAttribute('href') === target)[0];
      if (link) click(link); else location.hash = target;
      await settle();
      const page = snapshot();
      pages.push(page);
      for (const l of page.links) if (l.href && l.href.startsWith('#/') && !seen.has(l.href)) queue.push(l.href);
    }
    report({ pages, unvisited: queue.filter(h => !seen.has(h)).length });
  } else if (mode === 'steps') {
    const steps = JSON.parse(rest[0]);
    await open(steps[0].go);
    const pages = [snapshot()];
    for (const step of steps.slice(1)) {
      if (step.go !== undefined) location.hash = step.go;
      else if (step.click !== undefined) {
        const link = all(main, n => n instanceof ElementNode && n.tagName === 'A' && n.textContent === step.click)[0];
        if (!link) throw new Error('No link reads ' + step.click);
        click(link);
      }
      await settle();
      pages.push(snapshot());
    }
    report({ pages });
  } else {
    throw new Error('Unknown mode ' + mode);
  }
}

run().catch(error => { report({ failure: String(error && error.stack || error) }); process.exitCode = 1; });
