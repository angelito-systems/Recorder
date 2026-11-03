# Contribuir

Gracias por ayudar. Para que un cambio entre rápido:

1. **Abre un issue antes** de un cambio grande, para acordar el enfoque. Los arreglos pequeños pueden ir directo a PR.
2. **Compila y prueba**: .NET 10 SDK y `ffmpeg.exe` en el PATH (las pruebas hacen exportaciones reales).
   ```
   dotnet test
   dotnet run --project src/Angelito.ScreenStudio.Desktop
   ```
3. **Estilo**: imita el código que tocas. La lógica sin interfaz va en `Core` y lleva prueba; la app WPF va en `Desktop`. Comentarios solo para el *porqué*.
4. **Un PR, un cambio**, desde una rama propia hacia `master`. Si cambia lo hecho o lo pendiente, actualiza `docs/ROADMAP.md`.
5. **Privacidad**: la app graba pantallas y teclado. Nada que pueda registrar texto escrito, contraseñas o datos personales entra sin una prueba que lo demuestre.

Al contribuir aceptas que tu aporte se publique bajo la [licencia MIT](LICENSE).
