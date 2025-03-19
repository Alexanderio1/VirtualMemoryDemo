using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace VirtualMemoryDemo
{
    // Общие константы: число ячеек на странице – всегда 128, битовая карта – 16 байт,
    // для int страница данных фиксирована в 512 байт, а для char её размер зависит от длины элемента.
    public static class Constants
    {
        public const int CELLS_PER_PAGE = 128;
        public const int BITMAP_SIZE_BYTES = (CELLS_PER_PAGE + 7) / 8; // 128 бит -> 16 байт
        public const int BUFFER_PAGES = 3; // число страниц, хранящихся в оперативной памяти

        // Для int: фиксированная длина данных на странице – 512 байт (128 * 4)
        public const int PAGE_DATA_SIZE_INT = 512;
        public const int INT_SIZE = 4;
    }

    #region VirtualIntArray (тип int)
    // Страница для int-массива
    public class IntPage
    {
        public int AbsPageNumber { get; set; } = -1;
        public bool Modified { get; set; } = false;
        public DateTime LastAccessTime { get; set; }
        public bool[] Bitmap { get; set; }
        public int[] Data { get; set; }
        public bool Valid { get; set; } = false;

        public IntPage()
        {
            Bitmap = new bool[Constants.CELLS_PER_PAGE];
            Data = new int[Constants.CELLS_PER_PAGE];
        }
    }

    // Класс виртуального массива для типа int
    public class VirtualIntArray
    {
        private FileStream swapFile;         // Файловый поток для файла подкачки
        private string filename;             // Имя файла
        private long arraySize;              // Общее число элементов массива
        private int numPages;                // Число страниц (вычисляется по размеру массива)
        private List<IntPage> pageBuffer;    // Буфер страниц в оперативной памяти

        public VirtualIntArray(string fname, long arrSize)
        {
            filename = fname;
            arraySize = arrSize;
            numPages = (int)((arraySize + Constants.CELLS_PER_PAGE - 1) / Constants.CELLS_PER_PAGE);
            pageBuffer = new List<IntPage>();
            for (int i = 0; i < Constants.BUFFER_PAGES; i++)
            {
                pageBuffer.Add(new IntPage());
            }

            // Если файла нет, создаём его и инициализируем нулями
            if (!File.Exists(filename))
            {
                swapFile = new FileStream(filename, FileMode.Create, FileAccess.ReadWrite);
                // Записываем сигнатуру "VM"
                byte[] sig = new byte[2] { (byte)'V', (byte)'M' };
                swapFile.Write(sig, 0, sig.Length);

                byte[] zeroBitmap = new byte[Constants.BITMAP_SIZE_BYTES];
                byte[] zeroData = new byte[Constants.PAGE_DATA_SIZE_INT];
                for (int i = 0; i < numPages; i++)
                {
                    swapFile.Write(zeroBitmap, 0, zeroBitmap.Length);
                    swapFile.Write(zeroData, 0, zeroData.Length);
                }
                swapFile.Flush();
            }
            else
            {
                swapFile = new FileStream(filename, FileMode.Open, FileAccess.ReadWrite);
            }
        }

        // Вычисляет смещение в файле для заданной страницы
        private long GetPageOffset(int pageNum)
        {
            return 2 + pageNum * (Constants.BITMAP_SIZE_BYTES + Constants.PAGE_DATA_SIZE_INT);
        }

        // Чтение страницы из файла
        private void ReadPageFromDisk(int pageNum, IntPage page)
        {
            long offset = GetPageOffset(pageNum);
            swapFile.Seek(offset, SeekOrigin.Begin);

            byte[] bitmapBytes = new byte[Constants.BITMAP_SIZE_BYTES];
            int bytesRead = swapFile.Read(bitmapBytes, 0, Constants.BITMAP_SIZE_BYTES);
            if (bytesRead != Constants.BITMAP_SIZE_BYTES)
                throw new Exception("Ошибка чтения битовой карты");
            for (int i = 0; i < Constants.CELLS_PER_PAGE; i++)
            {
                int byteIndex = i / 8;
                int bitIndex = i % 8;
                page.Bitmap[i] = ((bitmapBytes[byteIndex] >> bitIndex) & 0x1) == 1;
            }

            byte[] dataBytes = new byte[Constants.PAGE_DATA_SIZE_INT];
            bytesRead = swapFile.Read(dataBytes, 0, Constants.PAGE_DATA_SIZE_INT);
            if (bytesRead != Constants.PAGE_DATA_SIZE_INT)
                throw new Exception("Ошибка чтения данных страницы");
            for (int i = 0; i < Constants.CELLS_PER_PAGE; i++)
            {
                page.Data[i] = BitConverter.ToInt32(dataBytes, i * Constants.INT_SIZE);
            }
            page.AbsPageNumber = pageNum;
            page.Modified = false;
            page.Valid = true;
            page.LastAccessTime = DateTime.Now;
        }

        // Запись страницы в файл
        private void WritePageToDisk(int pageNum, IntPage page)
        {
            long offset = GetPageOffset(pageNum);
            swapFile.Seek(offset, SeekOrigin.Begin);

            byte[] bitmapBytes = new byte[Constants.BITMAP_SIZE_BYTES];
            for (int i = 0; i < Constants.CELLS_PER_PAGE; i++)
            {
                int byteIndex = i / 8;
                int bitIndex = i % 8;
                if (page.Bitmap[i])
                    bitmapBytes[byteIndex] |= (byte)(1 << bitIndex);
            }
            swapFile.Write(bitmapBytes, 0, bitmapBytes.Length);

            byte[] dataBytes = new byte[Constants.PAGE_DATA_SIZE_INT];
            for (int i = 0; i < Constants.CELLS_PER_PAGE; i++)
            {
                byte[] intBytes = BitConverter.GetBytes(page.Data[i]);
                Buffer.BlockCopy(intBytes, 0, dataBytes, i * Constants.INT_SIZE, Constants.INT_SIZE);
            }
            swapFile.Write(dataBytes, 0, dataBytes.Length);
            swapFile.Flush();
            page.Modified = false;
        }

        // Получение страницы, содержащей элемент с данным индексом. Если страницы нет в буфере,
        // выбирается замещаемая (наименее недавно использованная), при необходимости она сбрасывается на диск.
        private (IntPage, int) GetPageForIndex(long index)
        {
            if (index >= arraySize)
                throw new IndexOutOfRangeException("Индекс выходит за границы массива");
            int pageNum = (int)(index / Constants.CELLS_PER_PAGE);
            int indexInPage = (int)(index % Constants.CELLS_PER_PAGE);

            foreach (var p in pageBuffer)
            {
                if (p.Valid && p.AbsPageNumber == pageNum)
                {
                    p.LastAccessTime = DateTime.Now;
                    return (p, indexInPage);
                }
            }
            IntPage replacePage = pageBuffer.FirstOrDefault(p => !p.Valid);
            if (replacePage == null)
            {
                replacePage = pageBuffer.OrderBy(p => p.LastAccessTime).First();
                if (replacePage.Modified)
                    WritePageToDisk(replacePage.AbsPageNumber, replacePage);
            }
            ReadPageFromDisk(pageNum, replacePage);
            return (replacePage, indexInPage);
        }

        // Чтение элемента массива по индексу
        public int ReadElement(long index)
        {
            var (page, indexInPage) = GetPageForIndex(index);
            if (!page.Bitmap[indexInPage])
                return 0;
            return page.Data[indexInPage];
        }

        // Запись значения в элемент массива
        public void WriteElement(long index, int value)
        {
            var (page, indexInPage) = GetPageForIndex(index);
            page.Data[indexInPage] = value;
            page.Bitmap[indexInPage] = true;
            page.Modified = true;
            page.LastAccessTime = DateTime.Now;
        }

        public void Flush()
        {
            foreach (var p in pageBuffer)
            {
                if (p.Valid && p.Modified)
                    WritePageToDisk(p.AbsPageNumber, p);
            }
        }

        public void Close()
        {
            Flush();
            swapFile.Close();
        }
    }
    #endregion

    #region VirtualCharArray (тип char фиксированной длины)
    // Страница для char-массива (фиксированная длина строки)
    public class CharPage
    {
        public int AbsPageNumber { get; set; } = -1;
        public bool Modified { get; set; } = false;
        public DateTime LastAccessTime { get; set; }
        public bool[] Bitmap { get; set; }
        public string[] Data { get; set; }
        public bool Valid { get; set; } = false;

        public CharPage(int cells)
        {
            Bitmap = new bool[cells];
            Data = new string[cells];
            for (int i = 0; i < cells; i++)
                Data[i] = "";
        }
    }

    // Класс виртуального массива для типа char (фиксированная длина строки)
    public class VirtualCharArray
    {
        private FileStream swapFile;
        private string filename;
        private long arraySize;
        private int fixedLength;     // Фиксированная длина строки
        private int numPages;
        private int pageDataSize;    // Размер области данных страницы (вычисляется как 128 * fixedLength, выровненный по 512)
        private List<CharPage> pageBuffer;

        public VirtualCharArray(string fname, long arrSize, int fixedLen)
        {
            filename = fname;
            arraySize = arrSize;
            fixedLength = fixedLen;
            // Вычисляем "сырую" длину страницы: 128 элементов по fixedLength байт
            int rawSize = Constants.CELLS_PER_PAGE * fixedLength;
            // Выравнивание до ближайшего числа, кратного 512
            pageDataSize = ((rawSize + 511) / 512) * 512;
            numPages = (int)((arraySize + Constants.CELLS_PER_PAGE - 1) / Constants.CELLS_PER_PAGE);
            pageBuffer = new List<CharPage>();
            for (int i = 0; i < Constants.BUFFER_PAGES; i++)
            {
                pageBuffer.Add(new CharPage(Constants.CELLS_PER_PAGE));
            }

            if (!File.Exists(filename))
            {
                swapFile = new FileStream(filename, FileMode.Create, FileAccess.ReadWrite);
                byte[] sig = new byte[2] { (byte)'V', (byte)'M' };
                swapFile.Write(sig, 0, sig.Length);

                byte[] zeroBitmap = new byte[Constants.BITMAP_SIZE_BYTES];
                byte[] zeroData = new byte[pageDataSize];
                for (int i = 0; i < numPages; i++)
                {
                    swapFile.Write(zeroBitmap, 0, zeroBitmap.Length);
                    swapFile.Write(zeroData, 0, zeroData.Length);
                }
                swapFile.Flush();
            }
            else
            {
                swapFile = new FileStream(filename, FileMode.Open, FileAccess.ReadWrite);
            }
        }

        private long GetPageOffset(int pageNum)
        {
            return 2 + pageNum * (Constants.BITMAP_SIZE_BYTES + pageDataSize);
        }

        private void ReadPageFromDisk(int pageNum, CharPage page)
        {
            long offset = GetPageOffset(pageNum);
            swapFile.Seek(offset, SeekOrigin.Begin);

            byte[] bitmapBytes = new byte[Constants.BITMAP_SIZE_BYTES];
            int bytesRead = swapFile.Read(bitmapBytes, 0, Constants.BITMAP_SIZE_BYTES);
            if (bytesRead != Constants.BITMAP_SIZE_BYTES)
                throw new Exception("Ошибка чтения битовой карты");
            for (int i = 0; i < Constants.CELLS_PER_PAGE; i++)
            {
                int byteIndex = i / 8;
                int bitIndex = i % 8;
                page.Bitmap[i] = ((bitmapBytes[byteIndex] >> bitIndex) & 0x1) == 1;
            }

            byte[] dataBytes = new byte[pageDataSize];
            bytesRead = swapFile.Read(dataBytes, 0, pageDataSize);
            if (bytesRead != pageDataSize)
                throw new Exception("Ошибка чтения данных страницы");

            page.Data = new string[Constants.CELLS_PER_PAGE];
            for (int i = 0; i < Constants.CELLS_PER_PAGE; i++)
            {
                // Каждый элемент занимает fixedLength байт, начиная с позиции i * fixedLength
                byte[] strBytes = new byte[fixedLength];
                Array.Copy(dataBytes, i * fixedLength, strBytes, 0, fixedLength);
                // Преобразуем байты в строку (используем ASCII), обрезая завершающие нули
                string s = Encoding.ASCII.GetString(strBytes).TrimEnd('\0');
                page.Data[i] = s;
            }
            page.AbsPageNumber = pageNum;
            page.Modified = false;
            page.Valid = true;
            page.LastAccessTime = DateTime.Now;
        }

        private void WritePageToDisk(int pageNum, CharPage page)
        {
            long offset = GetPageOffset(pageNum);
            swapFile.Seek(offset, SeekOrigin.Begin);

            byte[] bitmapBytes = new byte[Constants.BITMAP_SIZE_BYTES];
            for (int i = 0; i < Constants.CELLS_PER_PAGE; i++)
            {
                int byteIndex = i / 8;
                int bitIndex = i % 8;
                if (page.Bitmap[i])
                    bitmapBytes[byteIndex] |= (byte)(1 << bitIndex);
            }
            swapFile.Write(bitmapBytes, 0, bitmapBytes.Length);

            byte[] dataBytes = new byte[pageDataSize];
            for (int i = 0; i < Constants.CELLS_PER_PAGE; i++)
            {
                byte[] strBytes = new byte[fixedLength];
                if (page.Bitmap[i] && !string.IsNullOrEmpty(page.Data[i]))
                {
                    byte[] temp = Encoding.ASCII.GetBytes(page.Data[i]);
                    int len = Math.Min(temp.Length, fixedLength);
                    Array.Copy(temp, strBytes, len);
                }
                // Копируем strBytes в dataBytes в позицию i * fixedLength
                Array.Copy(strBytes, 0, dataBytes, i * fixedLength, fixedLength);
            }
            swapFile.Write(dataBytes, 0, dataBytes.Length);
            swapFile.Flush();
            page.Modified = false;
        }

        private (CharPage, int) GetPageForIndex(long index)
        {
            if (index >= arraySize)
                throw new IndexOutOfRangeException("Индекс выходит за границы массива");
            int pageNum = (int)(index / Constants.CELLS_PER_PAGE);
            int indexInPage = (int)(index % Constants.CELLS_PER_PAGE);

            foreach (var p in pageBuffer)
            {
                if (p.Valid && p.AbsPageNumber == pageNum)
                {
                    p.LastAccessTime = DateTime.Now;
                    return (p, indexInPage);
                }
            }
            CharPage replacePage = pageBuffer.FirstOrDefault(p => !p.Valid);
            if (replacePage == null)
            {
                replacePage = pageBuffer.OrderBy(p => p.LastAccessTime).First();
                if (replacePage.Modified)
                    WritePageToDisk(replacePage.AbsPageNumber, replacePage);
            }
            ReadPageFromDisk(pageNum, replacePage);
            return (replacePage, indexInPage);
        }

        public string ReadElement(long index)
        {
            var (page, indexInPage) = GetPageForIndex(index);
            if (!page.Bitmap[indexInPage])
                return "";
            return page.Data[indexInPage];
        }

        public void WriteElement(long index, string value)
        {
            var (page, indexInPage) = GetPageForIndex(index);
            // Если значение длиннее фиксированной длины, усекаем его
            if (value.Length > fixedLength)
                value = value.Substring(0, fixedLength);
            page.Data[indexInPage] = value;
            page.Bitmap[indexInPage] = true;
            page.Modified = true;
            page.LastAccessTime = DateTime.Now;
        }

        public void Flush()
        {
            foreach (var p in pageBuffer)
            {
                if (p.Valid && p.Modified)
                    WritePageToDisk(p.AbsPageNumber, p);
            }
        }

        public void Close()
        {
            Flush();
            swapFile.Close();
        }
    }
    #endregion

    #region VirtualVarcharArray (тип varchar – шаблон)
    // Для типа varchar (строки переменной длины) требуется использовать два файла:
    // один для хранения адресов строк (с аналогичной страничной организацией) и другой для самих строк,
    // где каждой строке предшествует 4-байтовая длина записи.
    // Ниже приведён лишь шаблон класса.
    public class VirtualVarcharArray
    {
        public VirtualVarcharArray(string fname, long arrSize, int maxLength)
        {
            throw new NotImplementedException("Реализация для varchar не реализована.");
        }

        public void WriteElement(long index, string value)
        {
            throw new NotImplementedException();
        }

        public string ReadElement(long index)
        {
            throw new NotImplementedException();
        }

        public void Close()
        {
        }
    }
    #endregion

    #region Тестирующая программа
    // Программа работает в консоли и поддерживает команды:
    // Create <имя файла> <тип> <размер массива>
    // Input <индекс> <значение>      – для int: число; для char/varchar: строка в кавычках
    // Print <индекс>
    // Exit
    public class Program
    {
        // Храним текущий виртуальный массив и его тип
        static object currentArray = null;
        static string currentType = "";
        static long currentSize = 0;

        public static void Main(string[] args)
        {
            Console.Write("VM> ");
            string commandLine;
            while ((commandLine = Console.ReadLine()) != null)
            {
                string[] parts = commandLine.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 0)
                {
                    Console.Write("VM> ");
                    continue;
                }
                string cmd = parts[0].ToLower();
                if (cmd == "create")
                {
                    // Формат: Create <имя файла> <тип> <размер массива>
                    if (parts.Length < 4)
                    {
                        Console.WriteLine("Использование: Create <имя файла> <тип> <размер массива>");
                    }
                    else
                    {
                        string fileName = parts[1];
                        string typeParam = parts[2].ToLower();
                        if (!long.TryParse(parts[3], out long arrSize))
                        {
                            Console.WriteLine("Неверный формат размера массива");
                        }
                        else
                        {
                            try
                            {
                                if (typeParam == "int")
                                {
                                    currentArray = new VirtualIntArray(fileName, arrSize);
                                    currentType = "int";
                                    currentSize = arrSize;
                                    Console.WriteLine($"Виртуальный массив int создан. Размер: {arrSize}");
                                }
                                else if (typeParam.StartsWith("char"))
                                {
                                    int start = typeParam.IndexOf('(');
                                    int end = typeParam.IndexOf(')');
                                    if (start != -1 && end != -1 && end > start)
                                    {
                                        string lenStr = typeParam.Substring(start + 1, end - start - 1);
                                        if (int.TryParse(lenStr, out int fixedLen))
                                        {
                                            currentArray = new VirtualCharArray(fileName, arrSize, fixedLen);
                                            currentType = "char";
                                            currentSize = arrSize;
                                            Console.WriteLine($"Виртуальный массив char({fixedLen}) создан. Размер: {arrSize}");
                                        }
                                        else
                                        {
                                            Console.WriteLine("Неверный формат длины строки для char");
                                        }
                                    }
                                    else
                                    {
                                        Console.WriteLine("Укажите длину строки в формате char(длина)");
                                    }
                                }
                                else if (typeParam.StartsWith("varchar"))
                                {
                                    int start = typeParam.IndexOf('(');
                                    int end = typeParam.IndexOf(')');
                                    if (start != -1 && end != -1 && end > start)
                                    {
                                        string lenStr = typeParam.Substring(start + 1, end - start - 1);
                                        if (int.TryParse(lenStr, out int maxLen))
                                        {
                                            currentArray = new VirtualVarcharArray(fileName, arrSize, maxLen);
                                            currentType = "varchar";
                                            currentSize = arrSize;
                                            Console.WriteLine($"Виртуальный массив varchar({maxLen}) создан. Размер: {arrSize}");
                                        }
                                        else
                                        {
                                            Console.WriteLine("Неверный формат максимальной длины для varchar");
                                        }
                                    }
                                    else
                                    {
                                        Console.WriteLine("Укажите максимальную длину в формате varchar(макс. длина)");
                                    }
                                }
                                else
                                {
                                    Console.WriteLine("Неподдерживаемый тип массива.");
                                }
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine("Ошибка создания виртуального массива: " + ex.Message);
                            }
                        }
                    }
                }
                else if (cmd == "input")
                {
                    // Для int: Input <индекс> <значение>
                    // Для char/varchar: Input <индекс> "<строка>"
                    if (currentArray == null)
                    {
                        Console.WriteLine("Сначала создайте виртуальный массив командой Create.");
                    }
                    else
                    {
                        if (currentType == "int")
                        {
                            if (parts.Length < 3)
                            {
                                Console.WriteLine("Использование: Input <индекс> <значение>");
                            }
                            else if (long.TryParse(parts[1], out long index) && int.TryParse(parts[2], out int value))
                            {
                                try
                                {
                                    ((VirtualIntArray)currentArray).WriteElement(index, value);
                                    Console.WriteLine($"Записано: [{index}] = {value}");
                                }
                                catch (Exception ex)
                                {
                                    Console.WriteLine("Ошибка записи: " + ex.Message);
                                }
                            }
                            else
                            {
                                Console.WriteLine("Неверный формат индекса или значения для int.");
                            }
                        }
                        else if (currentType == "char" || currentType == "varchar")
                        {
                            // Ожидается: Input <индекс> "<строка>"
                            if (parts.Length < 3)
                            {
                                Console.WriteLine("Использование: Input <индекс> \"<строка>\"");
                            }
                            else if (long.TryParse(parts[1], out long index))
                            {
                                int firstQuote = commandLine.IndexOf('\"');
                                int lastQuote = commandLine.LastIndexOf('\"');
                                if (firstQuote != -1 && lastQuote != -1 && lastQuote > firstQuote)
                                {
                                    string strValue = commandLine.Substring(firstQuote + 1, lastQuote - firstQuote - 1);
                                    try
                                    {
                                        if (currentType == "char")
                                        {
                                            ((VirtualCharArray)currentArray).WriteElement(index, strValue);
                                            Console.WriteLine($"Записано: [{index}] = \"{strValue}\"");
                                        }
                                        else
                                        {
                                            ((VirtualVarcharArray)currentArray).WriteElement(index, strValue);
                                            Console.WriteLine($"Записано: [{index}] = \"{strValue}\"");
                                        }
                                    }
                                    catch (Exception ex)
                                    {
                                        Console.WriteLine("Ошибка записи: " + ex.Message);
                                    }
                                }
                                else
                                {
                                    Console.WriteLine("Строковое значение должно быть в кавычках.");
                                }
                            }
                            else
                            {
                                Console.WriteLine("Неверный формат индекса.");
                            }
                        }
                    }
                }
                else if (cmd == "print")
                {
                    // Для всех типов: Print <индекс>
                    if (currentArray == null)
                    {
                        Console.WriteLine("Сначала создайте виртуальный массив командой Create.");
                    }
                    else
                    {
                        if (parts.Length < 2)
                        {
                            Console.WriteLine("Использование: Print <индекс>");
                        }
                        else if (long.TryParse(parts[1], out long index))
                        {
                            try
                            {
                                if (currentType == "int")
                                {
                                    int val = ((VirtualIntArray)currentArray).ReadElement(index);
                                    Console.WriteLine($"Элемент [{index}] = {val}");
                                }
                                else if (currentType == "char")
                                {
                                    string val = ((VirtualCharArray)currentArray).ReadElement(index);
                                    Console.WriteLine($"Элемент [{index}] = \"{val}\"");
                                }
                                else if (currentType == "varchar")
                                {
                                    string val = ((VirtualVarcharArray)currentArray).ReadElement(index);
                                    Console.WriteLine($"Элемент [{index}] = \"{val}\"");
                                }
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine("Ошибка чтения: " + ex.Message);
                            }
                        }
                        else
                        {
                            Console.WriteLine("Неверный формат индекса.");
                        }
                    }
                }
                else if (cmd == "exit")
                {
                    break;
                }
                else
                {
                    Console.WriteLine("Неизвестная команда.");
                }
                Console.Write("VM> ");
            }
            // При завершении закрываем созданный виртуальный массив
            if (currentArray != null)
            {
                if (currentType == "int")
                    ((VirtualIntArray)currentArray).Close();
                else if (currentType == "char")
                    ((VirtualCharArray)currentArray).Close();
                else if (currentType == "varchar")
                    ((VirtualVarcharArray)currentArray).Close();
            }
        }
    }
    #endregion
}
