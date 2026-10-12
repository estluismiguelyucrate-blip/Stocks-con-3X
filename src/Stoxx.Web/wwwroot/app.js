const state = { user: null, products: [], suppliers: [], cart: [], cash: null, view: 'inicio' };
const money = new Intl.NumberFormat('es-BO', { style: 'currency', currency: 'BOB' });
const dateTime = new Intl.DateTimeFormat('es-BO', { dateStyle: 'short', timeStyle: 'short' });
const roleNames = { Admin: 'Administrador', Cajero: 'Cajero', Inventario: 'Inventario' };

const $ = (selector, root = document) => root.querySelector(selector);
const $$ = (selector, root = document) => [...root.querySelectorAll(selector)];

async function api(path, options = {}) {
  const headers = new Headers(options.headers || {});
  if (options.body !== undefined) headers.set('Content-Type', 'application/json');
  const response = await fetch(`/api${path}`, { ...options, headers, credentials: 'same-origin' });
  let body = null;
  if (response.status !== 204) body = await response.json().catch(() => null);
  if (!response.ok) {
    if (response.status === 401 && state.user) showAuth();
    throw new Error(body?.message || (response.status === 401 ? 'La sesión terminó. Inicia sesión nuevamente.' : 'No se pudo completar la operación.'));
  }
  return body;
}

function escapeHtml(value) {
  return String(value ?? '').replace(/[&<>"']/g, char => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[char]));
}

function showToast(message, isError = false) {
  const toast = $('#toast');
  toast.textContent = message;
  toast.className = `toast${isError ? ' error' : ''}`;
  window.clearTimeout(showToast.timer);
  showToast.timer = window.setTimeout(() => toast.classList.add('hidden'), 3400);
}

function showFormMessage(element, message, type = 'error') {
  element.textContent = message;
  element.className = `alert ${type}`;
  element.classList.remove('hidden');
}

function showAuth(message = '') {
  state.user = null;
  $('#app-shell').classList.add('hidden');
  $('#auth-screen').classList.remove('hidden');
  $('#auth-alert').classList.add('hidden');
  if (message) showFormMessage($('#auth-alert'), message);
}

function setAuthMode(setupRequired) {
  $('#auth-title').textContent = setupRequired ? 'Configura el acceso' : 'Iniciar sesión';
  $('#auth-description').textContent = setupRequired
    ? 'Crea la primera cuenta. Tendrá permisos de administrador.'
    : 'Ingresa con tu cuenta para administrar la tienda.';
  $('#setup-form').classList.toggle('hidden', !setupRequired);
  $('#login-form').classList.toggle('hidden', setupRequired);
}

async function initialize() {
  try {
    const status = await api('/auth/status');
    if (status.authenticated) {
      state.user = status.user;
      await enterApp();
    } else {
      showAuth();
      setAuthMode(status.setupRequired);
    }
  } catch (error) {
    showAuth(error.message);
    setAuthMode(false);
  }
}

async function enterApp() {
  $('#auth-screen').classList.add('hidden');
  $('#app-shell').classList.remove('hidden');
  $('#user-name').textContent = state.user.name;
  $('#user-role').textContent = roleNames[state.user.role] || state.user.role;
  $('#user-avatar').textContent = state.user.name.trim().charAt(0).toUpperCase();
  const isAdmin = state.user.role === 'Admin';
  const isCashier = state.user.role === 'Cajero';
  const canInventory = isAdmin || state.user.role === 'Inventario';
  $$('.admin-only').forEach(element => element.classList.toggle('hidden', !isAdmin));
  $$('[data-view="ventas"]').forEach(element => element.classList.toggle('hidden', !(isAdmin || isCashier)));
  $$('[data-view="productos"]').forEach(element => element.classList.toggle('hidden', !canInventory));
  $$('[data-view="compras"]').forEach(element => element.classList.toggle('hidden', !canInventory));
  $$('[data-view="reportes"]').forEach(element => element.classList.toggle('hidden', !(isAdmin || isCashier)));
  $$('.admin-cash').forEach(element => element.classList.toggle('hidden', !(isAdmin || isCashier)));
  $('#product-form').classList.toggle('hidden', !canInventory);
  $('#supplier-form').classList.toggle('hidden', !canInventory);
  $('#purchase-form').classList.toggle('hidden', !canInventory);
  $('#sale-form').classList.toggle('hidden', !(isAdmin || isCashier));
  await goTo('inicio');
}

async function goTo(view) {
  const allowed = $(`.nav-link[data-view="${view}"]`);
  if (!allowed || allowed.classList.contains('hidden')) return;
  state.view = view;
  $$('.view').forEach(section => section.classList.toggle('hidden', section.id !== `view-${view}`));
  $$('.nav-link').forEach(button => button.classList.toggle('active', button.dataset.view === view));
  const titles = { inicio: 'Resumen', ventas: 'Registrar venta', productos: 'Productos', compras: 'Compras y proveedores', reportes: 'Reportes', usuarios: 'Usuarios' };
  $('#page-title').textContent = titles[view] || 'Resumen';
  try {
    if (view === 'inicio') await loadDashboard();
    if (view === 'ventas') await loadSalesView();
    if (view === 'productos') await loadProductsView();
    if (view === 'compras') await loadPurchasesView();
    if (view === 'reportes') await loadReport();
    if (view === 'usuarios') await loadUsers();
  } catch (error) { showToast(error.message, true); }
}

async function loadDashboard() {
  const [dashboard, cash] = await Promise.all([api('/dashboard'), api('/cash')]);
  state.cash = cash;
  $('#today-label').textContent = new Intl.DateTimeFormat('es-BO', { dateStyle: 'full' }).format(new Date());
  $('#metric-sales').textContent = money.format(dashboard.salesTotal);
  $('#metric-sales-count').textContent = `${dashboard.salesCount} ${dashboard.salesCount === 1 ? 'venta' : 'ventas'}`;
  $('#metric-profit').textContent = money.format(dashboard.grossProfit);
  $('#metric-products').textContent = dashboard.activeProducts;
  $('#metric-low').textContent = dashboard.lowStockCount;
  renderCash(cash);
  $('#low-stock-list').innerHTML = dashboard.lowStock.length
    ? dashboard.lowStock.map(product => `<div class="compact-row"><div><strong>${escapeHtml(product.name)}</strong><small>Código ${escapeHtml(product.code)} · mínimo ${product.minimumStock}</small></div><span class="pill warn">${product.stock} uds.</span></div>`).join('')
    : '<div class="empty-state">No hay productos bajo el mínimo.</div>';
  $('#recent-sales').innerHTML = dashboard.recentSales.length
    ? dashboard.recentSales.map(sale => `<tr><td>#${sale.id}</td><td>${formatDate(sale.createdAtUtc)}</td><td>${escapeHtml(sale.cashierName)}</td><td>${escapeHtml(sale.paymentMethod)}</td><td class="numeric">${money.format(sale.total)}</td></tr>`).join('')
    : '<tr><td colspan="5"><div class="empty-state">Todavía no hay ventas para mostrar.</div></td></tr>';
}

function renderCash(cash) {
  state.cash = cash;
  const isOpen = cash.open === true;
  $('#cash-title').textContent = isOpen ? 'Caja abierta' : 'Caja cerrada';
  $('#cash-status').textContent = isOpen ? 'Abierta' : 'Cerrada';
  $('#cash-status').className = `pill ${isOpen ? 'good' : ''}`;
  $('#sale-cash-pill').textContent = isOpen ? 'Caja abierta' : 'Abre caja para vender';
  $('#sale-cash-pill').className = `pill ${isOpen ? 'good' : 'warn'}`;
  $('#cash-details').innerHTML = isOpen
    ? `<div><span>Fondo inicial</span><strong>${money.format(cash.openingAmount)}</strong></div><div><span>Efectivo esperado</span><strong>${money.format(cash.expectedCash)}</strong></div><div><span>Abrió</span><strong>${escapeHtml(cash.openedBy)}</strong></div>`
    : '<p class="muted">Registra el monto inicial para habilitar las ventas.</p>';
  $('#cash-open-form').classList.toggle('hidden', isOpen || !['Admin', 'Cajero'].includes(state.user.role));
  $('#cash-close-form').classList.toggle('hidden', !isOpen || !['Admin', 'Cajero'].includes(state.user.role));
  $('#sale-form button[type="submit"]').disabled = !isOpen;
}

async function loadSalesView() {
  const [products, cash] = await Promise.all([api('/products'), api('/cash')]);
  state.products = products;
  renderCash(cash);
  renderSaleProducts();
  renderCart();
}

function renderSaleProducts() {
  const query = $('#sale-search').value.trim().toLocaleLowerCase();
  const available = state.products.filter(product => product.active && product.stock > 0 && `${product.code} ${product.name}`.toLocaleLowerCase().includes(query));
  $('#sale-product-list').innerHTML = available.length
    ? available.map(product => `<div class="pick-item"><div><strong>${escapeHtml(product.name)}</strong><small>${escapeHtml(product.code)} · ${product.stock} disponibles · ${money.format(product.unitPrice)}</small></div><button class="button secondary" type="button" data-add-product="${product.id}">Agregar</button></div>`).join('')
    : '<div class="empty-state">No hay productos disponibles con esa búsqueda.</div>';
}

function renderCart() {
  const total = state.cart.reduce((sum, line) => sum + line.unitPrice * line.quantity, 0);
  $('#cart-total').textContent = money.format(total);
  $('#cart-list').innerHTML = state.cart.length
    ? state.cart.map(line => `<div class="cart-item"><div><strong>${escapeHtml(line.name)}</strong><small>${money.format(line.unitPrice)} c/u</small></div><div class="quantity-control"><button type="button" aria-label="Restar" data-cart-change="${line.productId}" data-delta="-1">−</button><span>${line.quantity}</span><button type="button" aria-label="Sumar" data-cart-change="${line.productId}" data-delta="1">+</button></div><span class="cart-item-price">${money.format(line.unitPrice * line.quantity)}</span></div>`).join('')
    : '<div class="empty-state">Agrega productos para iniciar la venta.</div>';
}

async function loadProductsView() {
  state.products = await api('/products?all=true');
  renderProducts();
}

function renderProducts() {
  const query = $('#product-search').value.trim().toLocaleLowerCase();
  const products = state.products.filter(product => `${product.code} ${product.name} ${product.category}`.toLocaleLowerCase().includes(query));
  $('#products-table').innerHTML = products.length
    ? products.map(product => `<tr><td class="product-cell"><strong>${escapeHtml(product.name)}</strong><small>${escapeHtml(product.category || 'Sin categoría')}</small></td><td>${escapeHtml(product.code)}</td><td class="numeric">${money.format(product.unitPrice)}</td><td class="numeric"><span class="stock-badge ${product.lowStock ? 'low' : ''}">${product.stock}</span></td><td><span class="pill ${product.active ? 'good' : ''}">${product.active ? (product.lowStock ? 'Stock bajo' : 'Activo') : 'Inactivo'}</span></td><td>${product.active && ['Admin', 'Inventario'].includes(state.user.role) ? `<button class="table-action" data-edit-product="${product.id}">Editar</button><button class="table-action danger" data-archive-product="${product.id}">Desactivar</button>` : ''}</td></tr>`).join('')
    : '<tr><td colspan="6"><div class="empty-state">No hay productos registrados.</div></td></tr>';
}

async function loadPurchasesView() {
  const [products, suppliers, purchases] = await Promise.all([api('/products'), api('/suppliers'), api('/purchases')]);
  state.products = products;
  state.suppliers = suppliers;
  fillOptions($('#purchase-form [name="supplierId"]'), suppliers, 'Selecciona un proveedor', x => x.name);
  fillOptions($('#purchase-form [name="productId"]'), products, 'Selecciona un producto', x => `${x.name} · stock ${x.stock}`);
  $('#supplier-list').innerHTML = suppliers.length
    ? suppliers.map(supplier => `<div class="compact-row"><div><strong>${escapeHtml(supplier.name)}</strong><small>${escapeHtml(supplier.phone || supplier.email || 'Sin contacto registrado')}</small></div><button class="table-action danger" data-archive-supplier="${supplier.id}">Desactivar</button></div>`).join('')
    : '<div class="empty-state">No hay proveedores registrados.</div>';
  $('#purchases-table').innerHTML = purchases.length
    ? purchases.map(purchase => `<tr><td>${formatDate(purchase.createdAtUtc)}</td><td>${escapeHtml(purchase.supplierName)}</td><td>${escapeHtml(purchase.productName)}</td><td class="numeric">${purchase.quantity}</td><td class="numeric">${money.format(purchase.totalCost)}</td></tr>`).join('')
    : '<tr><td colspan="5"><div class="empty-state">No hay compras registradas.</div></td></tr>';
}

function fillOptions(select, items, placeholder, label) {
  const selected = select.value;
  select.innerHTML = `<option value="">${escapeHtml(placeholder)}</option>${items.map(item => `<option value="${item.id}">${escapeHtml(label(item))}</option>`).join('')}`;
  if (items.some(item => String(item.id) === selected)) select.value = selected;
}

async function loadReport() {
  const date = $('#report-date').value || localDateString();
  $('#report-date').value = date;
  const report = await api(`/reports/daily?date=${encodeURIComponent(date)}`);
  $('#report-total').textContent = money.format(report.totalSales);
  $('#report-count').textContent = `${report.saleCount} ${report.saleCount === 1 ? 'venta' : 'ventas'}`;
  $('#report-cash').textContent = money.format(report.cashTotal);
  $('#report-qr').textContent = money.format(report.qrTotal);
  $('#report-profit').textContent = money.format(report.grossProfit);
  $('#report-cost').textContent = `Costo de productos: ${money.format(report.costOfGoods)}`;
  $('#report-sales').innerHTML = report.sales.length
    ? report.sales.map(sale => `<tr><td>#${sale.id}</td><td>${formatDate(sale.createdAtUtc)}</td><td>${escapeHtml(sale.paymentMethod)}</td><td class="report-sale-items">${sale.items.map(item => `${escapeHtml(item.productName)} × ${item.quantity}`).join('<br>')}</td><td class="numeric">${money.format(sale.total)}</td></tr>`).join('')
    : '<tr><td colspan="5"><div class="empty-state">No hay ventas para esta fecha.</div></td></tr>';
  $('#top-products').innerHTML = report.topProducts.length
    ? report.topProducts.map(product => `<div class="compact-row"><div><strong>${escapeHtml(product.product)}</strong><small>${product.units} unidades</small></div><span class="compact-value">${money.format(product.total)}</span></div>`).join('')
    : '<div class="empty-state">No hay productos vendidos.</div>';
}

async function loadUsers() {
  const users = await api('/users');
  $('#users-table').innerHTML = users.length
    ? users.map(user => `<tr><td>${escapeHtml(user.name)}</td><td>${escapeHtml(user.userName)}</td><td><span class="pill">${escapeHtml(roleNames[user.role] || user.role)}</span></td></tr>`).join('')
    : '<tr><td colspan="3"><div class="empty-state">No hay usuarios registrados.</div></td></tr>';
}

function formatDate(value) {
  return dateTime.format(new Date(value));
}

function localDateString() {
  const now = new Date();
  const offset = now.getTimezoneOffset() * 60000;
  return new Date(now.getTime() - offset).toISOString().slice(0, 10);
}

function resetProductForm() {
  $('#product-form').reset();
  $('#product-form [name="id"]').value = '';
  $('#product-form-title').textContent = 'Nuevo producto';
  $('#cancel-product-edit').classList.add('hidden');
}

$('#setup-form').addEventListener('submit', async event => {
  event.preventDefault();
  const form = event.currentTarget;
  const data = Object.fromEntries(new FormData(form));
  try {
    await api('/auth/setup', { method: 'POST', body: JSON.stringify(data) });
    form.reset();
    setAuthMode(false);
    showFormMessage($('#auth-alert'), 'Administrador creado. Ya puedes iniciar sesión.', 'success');
  } catch (error) { showFormMessage($('#auth-alert'), error.message); }
});

$('#login-form').addEventListener('submit', async event => {
  event.preventDefault();
  const data = Object.fromEntries(new FormData(event.currentTarget));
  try {
    await api('/auth/login', { method: 'POST', body: JSON.stringify(data) });
    state.user = await api('/auth/status').then(result => result.user);
    await enterApp();
  } catch (error) {
    showFormMessage($('#auth-alert'), error.message === 'No se pudo completar la operación.' ? 'Usuario o contraseña incorrectos.' : error.message);
  }
});

$('#logout-button').addEventListener('click', async () => {
  try { await api('/auth/logout', { method: 'POST' }); } catch { /* La sesión puede haber expirado. */ }
  state.cart = [];
  showAuth();
  setAuthMode(false);
});

$('#main-nav').addEventListener('click', event => {
  const button = event.target.closest('[data-view]');
  if (button) goTo(button.dataset.view);
});

document.body.addEventListener('click', async event => {
  const viewButton = event.target.closest('.text-button[data-view], [data-refresh]');
  if (viewButton) {
    if (viewButton.dataset.view) goTo(viewButton.dataset.view);
    else goTo(state.view);
    return;
  }
  const addButton = event.target.closest('[data-add-product]');
  if (addButton) {
    const product = state.products.find(item => item.id === Number(addButton.dataset.addProduct));
    if (!product) return;
    const line = state.cart.find(item => item.productId === product.id);
    if (line) {
      if (line.quantity >= product.stock) return showToast('No hay más unidades disponibles.', true);
      line.quantity += 1;
    } else state.cart.push({ productId: product.id, name: product.name, unitPrice: product.unitPrice, quantity: 1, stock: product.stock });
    renderCart();
    return;
  }
  const changeButton = event.target.closest('[data-cart-change]');
  if (changeButton) {
    const line = state.cart.find(item => item.productId === Number(changeButton.dataset.cartChange));
    if (!line) return;
    const quantity = line.quantity + Number(changeButton.dataset.delta);
    if (quantity <= 0) state.cart = state.cart.filter(item => item !== line);
    else if (quantity <= line.stock) line.quantity = quantity;
    else showToast('No hay más unidades disponibles.', true);
    renderCart();
    return;
  }
  const editButton = event.target.closest('[data-edit-product]');
  if (editButton) {
    const product = state.products.find(item => item.id === Number(editButton.dataset.editProduct));
    if (!product) return;
    const form = $('#product-form');
    for (const key of ['id', 'code', 'name', 'category', 'costPrice', 'unitPrice', 'stock', 'minimumStock']) form.elements[key].value = product[key];
    $('#product-form-title').textContent = 'Editar producto';
    $('#cancel-product-edit').classList.remove('hidden');
    form.scrollIntoView({ behavior: 'smooth', block: 'start' });
    return;
  }
  const archiveProduct = event.target.closest('[data-archive-product]');
  if (archiveProduct && confirm('¿Desactivar este producto? Se conservará en el historial de ventas.')) {
    try { await api(`/products/${archiveProduct.dataset.archiveProduct}`, { method: 'DELETE' }); showToast('Producto desactivado.'); await loadProductsView(); }
    catch (error) { showToast(error.message, true); }
    return;
  }
  const archiveSupplier = event.target.closest('[data-archive-supplier]');
  if (archiveSupplier && confirm('¿Desactivar este proveedor?')) {
    try { await api(`/suppliers/${archiveSupplier.dataset.archiveSupplier}`, { method: 'DELETE' }); showToast('Proveedor desactivado.'); await loadPurchasesView(); }
    catch (error) { showToast(error.message, true); }
  }
});

$('#cash-open-form').addEventListener('submit', async event => {
  event.preventDefault();
  const openingAmount = Number(new FormData(event.currentTarget).get('openingAmount'));
  try { await api('/cash/open', { method: 'POST', body: JSON.stringify({ openingAmount }) }); showToast('Caja abierta.'); await loadDashboard(); }
  catch (error) { showToast(error.message, true); }
});

$('#cash-close-form').addEventListener('submit', async event => {
  event.preventDefault();
  const countedAmount = Number(new FormData(event.currentTarget).get('countedAmount'));
  try {
    const result = await api('/cash/close', { method: 'POST', body: JSON.stringify({ countedAmount }) });
    showToast(`Caja cerrada. Diferencia: ${money.format(result.difference)}.`);
    event.currentTarget.reset();
    await loadDashboard();
  } catch (error) { showToast(error.message, true); }
});

$('#sale-search').addEventListener('input', renderSaleProducts);
$('#sale-form').addEventListener('submit', async event => {
  event.preventDefault();
  if (!state.cart.length) return showToast('Agrega al menos un producto.', true);
  const paymentMethod = new FormData(event.currentTarget).get('paymentMethod');
  try {
    const result = await api('/sales', { method: 'POST', body: JSON.stringify({ paymentMethod, items: state.cart.map(line => ({ productId: line.productId, quantity: line.quantity })) }) });
    state.cart = [];
    renderCart();
    showToast(`Venta #${result.id} registrada por ${money.format(result.total)}.`);
    await loadSalesView();
  } catch (error) { showToast(error.message, true); }
});

$('#product-search').addEventListener('input', renderProducts);
$('#product-form').addEventListener('submit', async event => {
  event.preventDefault();
  const formData = Object.fromEntries(new FormData(event.currentTarget));
  const id = formData.id;
  const product = {
    code: formData.code, name: formData.name, category: formData.category || '',
    costPrice: Number(formData.costPrice), unitPrice: Number(formData.unitPrice),
    stock: Number(formData.stock), minimumStock: Number(formData.minimumStock)
  };
  try {
    await api(id ? `/products/${id}` : '/products', { method: id ? 'PUT' : 'POST', body: JSON.stringify(product) });
    resetProductForm();
    showToast(id ? 'Producto actualizado.' : 'Producto registrado.');
    await loadProductsView();
  } catch (error) { showToast(error.message, true); }
});
$('#cancel-product-edit').addEventListener('click', resetProductForm);

$('#supplier-form').addEventListener('submit', async event => {
  event.preventDefault();
  const data = Object.fromEntries(new FormData(event.currentTarget));
  try { await api('/suppliers', { method: 'POST', body: JSON.stringify(data) }); event.currentTarget.reset(); showToast('Proveedor registrado.'); await loadPurchasesView(); }
  catch (error) { showToast(error.message, true); }
});

$('#purchase-form').addEventListener('submit', async event => {
  event.preventDefault();
  const data = Object.fromEntries(new FormData(event.currentTarget));
  const request = { supplierId: Number(data.supplierId), productId: Number(data.productId), quantity: Number(data.quantity), unitCost: Number(data.unitCost) };
  try { await api('/purchases', { method: 'POST', body: JSON.stringify(request) }); event.currentTarget.reset(); showToast('Compra registrada y stock actualizado.'); await loadPurchasesView(); }
  catch (error) { showToast(error.message, true); }
});

$('#report-date').value = localDateString();
$('#report-date').addEventListener('change', () => loadReport().catch(error => showToast(error.message, true)));

$('#user-form').addEventListener('submit', async event => {
  event.preventDefault();
  const data = Object.fromEntries(new FormData(event.currentTarget));
  try { await api('/users', { method: 'POST', body: JSON.stringify(data) }); event.currentTarget.reset(); showToast('Usuario creado.'); await loadUsers(); }
  catch (error) { showToast(error.message, true); }
});

initialize();
