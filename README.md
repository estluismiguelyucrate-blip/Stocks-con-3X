# Stocks con 3X

Aplicación web académica para registrar ventas, controlar inventario y consultar cierres de caja de un pequeño comercio.

## Problema y propósito

El documento del proyecto describe comercios que anotan ventas y existencias por separado. Al vender, el inventario puede no actualizarse, lo que dificulta detectar faltantes, planificar reposiciones y cuadrar la caja.

El sistema reúne ventas, inventario, proveedores, compras y reportes para automatizar esos registros. Está pensado para propietarios o administradores, vendedores o cajeros y responsables de inventario.

## Qué incluye esta versión

- Inicio de sesión y cuentas con roles de administrador, cajero e inventario.
- Configuración inicial para crear la primera cuenta administradora.
- Catálogo de productos, precios, existencias y umbrales de stock mínimo.
- Registro de ventas en efectivo o QR con descuento de existencias y control de stock insuficiente.
- Apertura y cierre de caja, con cálculo del efectivo esperado y diferencia frente al conteo.
- Registro de proveedores y compras recibidas que actualizan el inventario.
- Resumen diario, alertas de stock bajo, totales por forma de pago, costo de mercancía, ganancia bruta y productos vendidos.
- Base de datos SQLite local. El catálogo empieza vacío para que el equipo introduzca sus datos reales.

## Tecnologías elegidas para esta implementación

La presentación propone una aplicación web con C# y ASP.NET Core. Esta versión sigue esa propuesta y utiliza SQLite. El PDF contiene alternativas incompatibles en distintas secciones (Java, C++, escritorio y C#/ASP.NET Core web); el equipo debe confirmar cuál descripción conservar en la documentación académica.

## Requisitos

- .NET SDK 10.
- Windows, macOS o Linux con soporte para ASP.NET Core.
- Conexión a Internet la primera vez para restaurar el paquete de SQLite.

## Ejecutar localmente

Desde la raíz del repositorio:

```powershell
dotnet run --project src/Stoxx.Web/Stoxx.Web.csproj
```

Abre [http://localhost:5080](http://localhost:5080). En el primer inicio, crea la cuenta administradora. Luego agrega los productos y proveedores que usará la demostración. La base de datos se guarda en `src/Stoxx.Web/data/stoxx.db` y está excluida de Git.

El administrador puede crear cuentas de cajero y de inventario desde **Usuarios**. La aplicación no trae usuarios, contraseñas ni productos precargados.

## Acceso desde celulares y otras computadoras

El enlace `http://localhost:5080` solo abre la aplicación en la misma laptop donde se está ejecutando. Si se abre ese enlace desde un celular, `localhost` apunta al propio celular, no a la laptop.

La interfaz se adapta a pantallas de celulares y computadoras. Sin embargo, para que otro dispositivo entre, la aplicación debe estar configurada para aceptar conexiones desde la red local y ambos dispositivos deben estar en la misma red. Para entrar desde Internet se necesita publicar la aplicación en un servicio de alojamiento. **Esta versión todavía no está publicada para acceso público.**

Antes de publicar el sistema, el equipo debe definir cómo autorizará las cuentas. En el primer inicio, quien llegue a la pantalla de configuración puede crear la primera cuenta administradora; por eso, esa configuración debe completarse antes de exponer la aplicación públicamente.

## Flujo de demostración

1. Crea la cuenta administradora inicial.
2. Registra algunos productos con costo, precio, cantidad inicial y stock mínimo.
3. Registra al menos un proveedor y, si corresponde, una compra recibida.
4. Abre la caja con un monto inicial.
5. Registra una venta en efectivo o QR y comprueba que cambie el stock.
6. Consulta el resumen, las alertas y el reporte diario; cierra la caja ingresando el efectivo contado.

Usa datos de demostración claramente identificados y autorizados por tu equipo antes de exponer la aplicación en clase. No cargues información personal o financiera real.

## Estructura del repositorio

- `src/Stoxx.Web/`: aplicación ASP.NET Core, interfaz y persistencia SQLite.
- `docs/proyecto/`: documento del proyecto.
- `docs/presentacion/`: presentación del equipo.
- `docs/gantt/`: planificación editable y diagrama.
- `equipo/`: archivos de integrantes y responsabilidades.
- `tests/`: reservado para pruebas del proyecto.
- `resources/`: reservado para recursos compartidos.
- `QA/`: reservado para casos de prueba y evidencias.

## Documentos del equipo

- [Documento del proyecto](docs/proyecto/Proyecto_Programa_Unido_con_Gantt.pdf)
- [Presentación](docs/presentacion/Presentacion.pptx)
- [Gantt editable](docs/gantt/Diagrama_Gantt_tabla.xlsx)
- [Diagrama de Gantt](docs/gantt/Diagrama_Gantt.png)
- [Integrantes](equipo/)
