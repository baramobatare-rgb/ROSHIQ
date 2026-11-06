// В WPF-проектах SDK не добавляет System.IO в неявные using (конфликт с System.Windows.Shapes.Path),
// поэтому подключаем его явно для всего приложения.
global using System.IO;
