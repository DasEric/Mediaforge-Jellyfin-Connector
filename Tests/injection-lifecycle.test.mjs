import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import test from 'node:test';
import vm from 'node:vm';

const source = await readFile(new URL('../Jellyfin.Plugin.MediaForge/Web/injection.js', import.meta.url), 'utf8');

class EventTarget {
  constructor() { this.listeners = new Map(); }
  addEventListener(type, listener) {
    if (!this.listeners.has(type)) this.listeners.set(type, []);
    this.listeners.get(type).push(listener);
  }
  dispatchEvent(event) {
    for (const listener of this.listeners.get(event.type) || []) listener.call(this, event);
    return !event.defaultPrevented;
  }
}

class Element extends EventTarget {
  constructor(tagName, document) {
    super();
    this.tagName = tagName.toUpperCase();
    this.ownerDocument = document;
    this.parentNode = null;
    this.children = [];
    this.attributes = new Map();
    this.style = { cssText: '', display: '' };
    this.id = '';
    this._innerHTML = '';
  }
  appendChild(child) { child.parentNode = this; this.children.push(child); return child; }
  remove() {
    if (!this.parentNode) return;
    this.parentNode.children = this.parentNode.children.filter((child) => child !== this);
    this.parentNode = null;
  }
  setAttribute(name, value) { this.attributes.set(name, String(value)); }
  getAttribute(name) { return this.attributes.get(name) ?? null; }
  get isConnected() {
    let current = this;
    while (current) {
      if (current === this.ownerDocument.documentElement) return true;
      current = current.parentNode;
    }
    return false;
  }
  contains(candidate) {
    if (candidate === this) return true;
    return this.children.some((child) => child.contains(candidate));
  }
  focus() { this.ownerDocument.activeElement = this; }
  click() { this.dispatchEvent({ type: 'click', target: this, preventDefault() {}, stopPropagation() {} }); }
  querySelector(selector) {
    const matches = (element) => {
      if (selector === 'button') return element.tagName === 'BUTTON';
      const attribute = selector.match(/^\[([^=\]]+)(?:="([^"]+)")?\]$/);
      if (!attribute) return false;
      const value = element.getAttribute(attribute[1]);
      return attribute[2] === undefined ? value !== null : value === attribute[2];
    };
    for (const child of this.children) {
      if (matches(child)) return child;
      const nested = child.querySelector(selector);
      if (nested) return nested;
    }
    return null;
  }
  set innerHTML(value) {
    this._innerHTML = value;
    this.children = [];
    if (!value.includes('data-mediaforge-close')) return;
    const header = new Element('div', this.ownerDocument);
    const close = new Element('button', this.ownerDocument);
    close.setAttribute('data-mediaforge-close', '');
    header.appendChild(close);
    const content = new Element('div', this.ownerDocument);
    content.setAttribute('data-content', '');
    this.appendChild(header);
    this.appendChild(content);
  }
  get innerHTML() { return this._innerHTML; }
}

class Document extends EventTarget {
  constructor() {
    super();
    this.readyState = 'complete';
    this.baseURI = 'http://jellyfin.test/web/';
    this.documentElement = new Element('html', this);
    this.body = new Element('body', this);
    this.documentElement.appendChild(this.body);
    this.activeElement = null;
    this.appBar = new Element('header', this);
    this.appBar.getBoundingClientRect = () => ({ top: 0, bottom: 48 });
    this.body.appendChild(this.appBar);
  }
  createElement(tagName) { return new Element(tagName, this); }
  getElementById(id) {
    const visit = (node) => {
      if (node.id === id) return node;
      for (const child of node.children) {
        const found = visit(child);
        if (found) return found;
      }
      return null;
    };
    return visit(this.documentElement);
  }
  querySelector(selector) {
    if (selector.includes('.mainDrawer-scrollContainer')) return null;
    return null;
  }
  querySelectorAll(selector) {
    if (selector.includes('.MuiAppBar-root')) return [this.appBar];
    return [];
  }
}

function anchor(href) {
  const value = { href, closest(selector) { return selector === 'a[href]' ? value : null; } };
  return value;
}

function clickEvent(target) {
  return {
    type: 'click',
    target,
    defaultPrevented: false,
    preventDefault() { this.defaultPrevented = true; },
    stopPropagation() {}
  };
}

test('request overlay closes through its controls and Jellyfin navigation', async () => {
  const document = new Document();
  const window = new EventTarget();
  window.document = document;
  window.location = { pathname: '/home', search: '', hash: '' };
  window.visualViewport = new EventTarget();
  const trigger = new Element('a', document);
  document.body.appendChild(trigger);
  document.activeElement = trigger;

  let resolvePage;
  const pagePromise = new Promise((resolve) => { resolvePage = resolve; });
  const client = {
    getUrl(path) { return path; },
    fetch() { return pagePromise; }
  };
  window.ApiClient = client;
  const historyCallbacks = [];
  const resizeObservers = [];
  class MutationObserver { observe() {} }
  class ResizeObserver {
    constructor(callback) { this.callback = callback; this.disconnected = false; resizeObservers.push(this); }
    observe() {}
    disconnect() { this.disconnected = true; }
  }
  class CustomEvent { constructor(type) { this.type = type; } }
  const sandbox = {
    window,
    document,
    ApiClient: client,
    Events: { on(_target, type, callback) { if (type === 'HISTORY_UPDATE') historyCallbacks.push(callback); } },
    MutationObserver,
    ResizeObserver,
    CustomEvent,
    DOMParser: class {},
    URL,
    console,
    setTimeout,
    clearTimeout,
    setInterval(callback) { setTimeout(callback, 0); return 1; },
    clearInterval() {}
  };
  vm.createContext(sandbox);
  vm.runInContext(source, sandbox, { filename: 'injection.js' });
  await new Promise((resolve) => setTimeout(resolve, 10));
  vm.runInContext(source, sandbox, { filename: 'injection.js' });
  assert.equal(historyCallbacks.length, 1);

  const openRequest = () => {
    const event = clickEvent(anchor('http://jellyfin.test/web/#/mediaforge-requests'));
    document.dispatchEvent(event);
    assert.equal(event.defaultPrevented, true);
    const overlay = document.getElementById('mediaforge-requests-modal');
    assert.ok(overlay);
    assert.equal(overlay.style.top, '48px');
    return overlay;
  };

  let overlay = openRequest();
  assert.equal(openRequest(), overlay);
  const close = overlay.querySelector('[data-mediaforge-close]');
  assert.equal(document.activeElement, close);
  close.click();
  assert.equal(document.getElementById('mediaforge-requests-modal'), null);
  assert.equal(document.activeElement, trigger);
  assert.equal(resizeObservers.at(-1).disconnected, true);

  openRequest();
  const homeClick = clickEvent(anchor('http://jellyfin.test/web/home'));
  document.dispatchEvent(homeClick);
  assert.equal(homeClick.defaultPrevented, false);
  assert.equal(document.getElementById('mediaforge-requests-modal'), null);

  openRequest();
  document.dispatchEvent({ type: 'keydown', key: 'Escape', preventDefault() {}, stopPropagation() {} });
  assert.equal(document.getElementById('mediaforge-requests-modal'), null);

  openRequest();
  window.dispatchEvent({ type: 'popstate' });
  assert.equal(document.getElementById('mediaforge-requests-modal'), null);

  openRequest();
  window.dispatchEvent({ type: 'hashchange' });
  assert.equal(document.getElementById('mediaforge-requests-modal'), null);

  openRequest();
  window.location.pathname = '/movies';
  historyCallbacks[0]({ type: 'HISTORY_UPDATE' }, { location: { pathname: '/movies', search: '', hash: '' } });
  assert.equal(document.getElementById('mediaforge-requests-modal'), null);

  window.location.pathname = '/home';
  overlay = openRequest();
  window.location.pathname = '/series';
  document.dispatchEvent({ type: 'viewshow', target: document.appBar });
  assert.equal(document.getElementById('mediaforge-requests-modal'), null);

  resolvePage('<div data-role="page"></div>');
  await new Promise((resolve) => setTimeout(resolve, 0));
  assert.equal(document.getElementById('mediaforge-requests-modal'), null);
  assert.equal(overlay.isConnected, false);
});
