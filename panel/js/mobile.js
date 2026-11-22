/* ============================================================
   手机竖屏布局的行为部分（配合 css/mobile.css）
   ------------------------------------------------------------
   · ☰ 打开 / 关闭侧边抽屉，选完页面自动收起，点遮罩或按返回键也能收起
   · 顶栏里放不下的控件（用户、语言、刷新、检查更新、切换服务器）在窄屏时
     移进抽屉顶部，宽屏时原样放回 —— 移动的是同一个元素，事件和 id 不变
   · 底部导航：房间 / 玩家 / 聊天 / 举报 / 更多；文字取自侧栏（已翻译）
   · 表格变卡片：给每个单元格写上 data-label=列名，CSS 用它显示"列名：值"
   只在窗口宽度 ≤ 760px 时生效；桌面端什么都不做。
   ============================================================ */
(function () {
  'use strict';

  var MQ = window.matchMedia('(max-width: 760px)');
  var BOTTOM_TABS = ['games', 'players', 'chat', 'reports'];
  var moved = [];          // [{ el, parent, next }]
  var built = false;
  var labelQueued = false;

  function $(id) { return document.getElementById(id); }

  function build() {
    if (built) return;
    var topbar = document.querySelector('.topbar');
    var sidebar = $('sidebar');
    var app = $('app');
    if (!topbar || !sidebar || !app) return;
    built = true;

    var menuBtn = document.createElement('button');
    menuBtn.className = 'm-menu-btn';
    menuBtn.type = 'button';
    menuBtn.setAttribute('aria-label', 'Menu');
    menuBtn.textContent = '☰';
    menuBtn.addEventListener('click', toggle);
    topbar.insertBefore(menuBtn, topbar.firstChild);

    var title = document.createElement('span');
    title.className = 'm-page-title';
    title.id = 'mPageTitle';
    var brand = topbar.querySelector('.brand');
    topbar.insertBefore(title, brand ? brand.nextSibling : menuBtn.nextSibling);

    var backdrop = document.createElement('div');
    backdrop.className = 'm-backdrop';
    backdrop.id = 'mBackdrop';
    backdrop.addEventListener('click', close);
    sidebar.parentNode.insertBefore(backdrop, sidebar.nextSibling);

    var tools = document.createElement('div');
    tools.className = 'm-drawer-tools';
    tools.id = 'mDrawerTools';
    sidebar.insertBefore(tools, sidebar.firstChild);

    var nav = document.createElement('nav');
    nav.className = 'm-bottom-nav';
    nav.id = 'mBottomNav';
    BOTTOM_TABS.forEach(function (name) {
      var b = document.createElement('button');
      b.type = 'button';
      b.dataset.tab = name;
      b.innerHTML = '<span class="m-ico"></span><span></span>';
      b.addEventListener('click', function () { if (typeof window.switchTab === 'function') window.switchTab(name); });
      nav.appendChild(b);
    });
    var more = document.createElement('button');
    more.type = 'button';
    more.dataset.more = '1';
    more.innerHTML = '<span class="m-ico">☰</span><span>更多</span>';
    more.addEventListener('click', open);
    nav.appendChild(more);
    app.appendChild(nav);

    document.addEventListener('keydown', function (e) { if (e.key === 'Escape') close(); });
  }

  // ---------- 抽屉 ----------
  function open() {
    var s = $('sidebar');
    if (!s || !MQ.matches) return;
    s.classList.add('m-open');
    $('mBackdrop').classList.add('m-open');
    document.body.classList.add('m-drawer-open');
  }

  function close() {
    var s = $('sidebar');
    if (!s) return;
    s.classList.remove('m-open');
    var b = $('mBackdrop');
    if (b) b.classList.remove('m-open');
    document.body.classList.remove('m-drawer-open');
  }

  function toggle() {
    var s = $('sidebar');
    if (s && s.classList.contains('m-open')) close(); else open();
  }

  // ---------- 顶栏控件进出抽屉 ----------
  function toolElements() {
    return [
      $('userDisplay'),
      $('langSelect'),
      $('refreshBtn'),
      $('updateBtn'),
      document.querySelector('.topbar button[onclick="goToServers()"]'),
    ].filter(Boolean);
  }

  function moveToolsIntoDrawer() {
    if (moved.length) return;
    var tools = $('mDrawerTools');
    if (!tools) return;
    toolElements().forEach(function (el) {
      moved.push({ el: el, parent: el.parentNode, next: el.nextSibling });
      tools.appendChild(el);
    });
  }

  function restoreTools() {
    // 倒序放回，保证每个元素原来的"下一个兄弟"还在原位
    for (var i = moved.length - 1; i >= 0; i--) {
      var m = moved[i];
      if (m.next && m.next.parentNode === m.parent) m.parent.insertBefore(m.el, m.next);
      else m.parent.appendChild(m.el);
    }
    moved = [];
  }

  // ---------- 标题与底部导航高亮 ----------
  function tabLabel(name) {
    var t = document.querySelector('.sidebar .tab[data-tab="' + name + '"]');
    if (!t) return { ico: '', text: name };
    var ico = t.querySelector('.tab-ico');
    var spans = t.querySelectorAll('span');
    var text = spans.length > 1 ? spans[1].textContent : t.textContent;
    return { ico: ico ? ico.textContent : '', text: (text || '').trim() };
  }

  function sync() {
    if (!built) return;
    var current = window._currentTab;
    if (!current) {
      var active = document.querySelector('.sidebar .tab.active');
      current = active ? active.dataset.tab : 'games';
    }

    var title = $('mPageTitle');
    if (title) title.textContent = tabLabel(current).text;

    var nav = $('mBottomNav');
    if (!nav) return;
    var inBottom = BOTTOM_TABS.indexOf(current) >= 0;
    Array.prototype.forEach.call(nav.querySelectorAll('button'), function (b) {
      if (b.dataset.more) {
        b.classList.toggle('active', !inBottom);
        return;
      }
      var lbl = tabLabel(b.dataset.tab);
      b.children[0].textContent = lbl.ico;
      b.children[1].textContent = lbl.text;
      b.classList.toggle('active', b.dataset.tab === current);
    });
  }

  // ---------- 表格列名 ----------
  function labelTables() {
    labelQueued = false;
    if (!MQ.matches) return;
    var tables = document.querySelectorAll('.main table');
    Array.prototype.forEach.call(tables, function (table) {
      var head = table.tHead && table.tHead.rows[0];
      if (!head) return;
      var names = [];
      Array.prototype.forEach.call(head.cells, function (th) {
        var span = th.colSpan || 1;
        var text = (th.textContent || '').trim();
        for (var k = 0; k < span; k++) names.push(text);
      });
      Array.prototype.forEach.call(table.tBodies, function (tb) {
        Array.prototype.forEach.call(tb.rows, function (tr) {
          var col = 0;
          Array.prototype.forEach.call(tr.cells, function (td) {
            var span = td.colSpan || 1;
            if (span === 1) {
              var name = names[col] || '';
              if (td.getAttribute('data-label') !== name) td.setAttribute('data-label', name);
            }
            col += span;
          });
        });
      });
    });

    // 只为对齐按钮放的空白标签（&nbsp;）在单列布局里没用
    Array.prototype.forEach.call(document.querySelectorAll('.main .form-group > label'), function (l) {
      var blank = !(l.textContent || '').replace(/ /g, ' ').trim() && !l.querySelector('input,select,textarea');
      l.classList.toggle('m-empty-label', blank);
    });
  }

  function queueLabel() {
    if (labelQueued) return;
    labelQueued = true;
    // 用定时器而不是 requestAnimationFrame: 后者在页面不可见时会暂停, 标注就会卡住。
    setTimeout(labelTables, 30);
  }

  // ---------- 切换 ----------
  function apply() {
    build();
    if (!built) return;
    if (MQ.matches) {
      moveToolsIntoDrawer();
      queueLabel();
    } else {
      close();
      restoreTools();
    }
    sync();
  }

  function hookSwitchTab() {
    var orig = window.switchTab;
    if (typeof orig !== 'function' || orig.__mobileWrapped) return;
    var wrapped = function (name) {
      var r = orig.apply(this, arguments);
      if (MQ.matches) close();
      sync();
      queueLabel();
      return r;
    };
    wrapped.__mobileWrapped = true;
    window.switchTab = wrapped;
  }

  function init() {
    hookSwitchTab();
    apply();
    var main = document.querySelector('.main');
    if (main && window.MutationObserver) {
      new MutationObserver(queueLabel).observe(main, { childList: true, subtree: true });
    }
    // 语言切换会改侧栏文字：重新同步底部导航与标题
    var sidebar = $('sidebar');
    if (sidebar && window.MutationObserver) {
      new MutationObserver(function () { sync(); }).observe(sidebar, { characterData: true, childList: true, subtree: true });
    }
    if (MQ.addEventListener) MQ.addEventListener('change', apply); else if (MQ.addListener) MQ.addListener(apply);
  }

  window.mobileNav = { open: open, close: close, toggle: toggle, refresh: function () { sync(); queueLabel(); } };

  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', init);
  else init();
})();
