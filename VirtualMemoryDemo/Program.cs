using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;

namespace VirtualMemoryDemo
{
    // Класс с константами, используемыми в системе
    public static class Constants
    {
        public const int PAGE_DATA_SIZE = 512;                         // 512 байт данных на страницу
        public const int INT_SIZE = 4;                                 // размер int – 4 байта
        public const int CELLS_PER_PAGE = PAGE_DATA_SIZE / INT_SIZE;   // 512 / 4 = 128 ячеек на странице
        public const int BITMAP_SIZE_BYTES = (CELLS_PER_PAGE + 7) / 8;   // 128 бит = 16 байт
        public const int BUFFER_PAGES = 3;                             // число страниц в оперативном буфере
    }

    // Класс, описывающий страницу, находящуюся в памяти
    public class Page
    {
        public int AbsPageNumber { get; set; } = -1;      // Абсолютный номер страницы в файле подкачки
        public bool Modified { get; set; } = false;         // Флаг модификации страницы (true – если страница изменялась)
        public DateTime LastAccessTime { get; set; }        // Время последнего обращения к странице
        public bool[] Bitmap { get; set; }                  // Битовая карта для ячеек (каждый бит соответствует ячейке)
        public int[] Data { get; set; }                     // Массив данных (целых чисел) для ячеек страницы
        public bool Valid { get; set; } = false;            // Флаг, указывающий, что страница загружена (валидна)

        public Page()
        {
            Bitmap = new bool[Constants.CELLS_PER_PAGE];
            Data = new int[Constants.CELLS_PER_PAGE];
        }
    }

    // Класс, реализующий виртуальный массив целых чисел
    public class VirtualIntArray
    {
        private FileStream swapFile;         // Файловый поток для файла подкачки
        private string filename;             // Имя файла подкачки
        private long arraySize;              // Общее число элементов виртуального массива
        private int numPages;                // Количество страниц (вычисляется как округление вверх от arraySize / CELLS_PER_PAGE)
        private List<Page> pageBuffer;       // Буфер страниц в оперативной памяти

        // Конструктор:
        // - Если файла не существует, создается новый, записывается сигнатура "VM" и заполняется нулями.
        // - Если файл существует, он открывается для чтения и записи.
        public VirtualIntArray(string fname, long arrSize)
        {
            filename = fname;
            arraySize = arrSize;
            numPages = (int)((arraySize + Constants.CELLS_PER_PAGE - 1) / Constants.CELLS_PER_PAGE);
            pageBuffer = new List<Page>();

            // Инициализация буфера страниц (количество страниц = BUFFER_PAGES)
            for (int i = 0; i < Constants.BUFFER_PAGES; i++)
            {
                pageBuffer.Add(new Page());
            }

            // Если файла нет – создаём его и заполняем начальными данными
            if (!File.Exists(filename))
            {
                // Создаем файл с доступом на чтение и запись
                swapFile = new FileStream(filename, FileMode.Create, FileAccess.ReadWrite);
                // Записываем сигнатуру "VM" (2 байта)
                byte[] sig = new byte[2] { (byte)'V', (byte)'M' };
                swapFile.Write(sig, 0, sig.Length);

                // Для каждой страницы записываем битовую карту (16 байт) и данные (512 байт) – все нули
                byte[] zeroBitmap = new byte[Constants.BITMAP_SIZE_BYTES];
                byte[] zeroData = new byte[Constants.PAGE_DATA_SIZE];

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

        // Метод для сброса (выгрузки) всех модифицированных страниц из буфера на диск
        public void Flush()
        {
            foreach (var p in pageBuffer)
            {
                if (p.Valid && p.Modified)
                {
                    WritePageToDisk(p.AbsPageNumber, p);
                }
            }
        }

        // Вычисление смещения в файле для заданной страницы.
        // Формат файла:
        // - Первые 2 байта – сигнатура "VM"
        // - Затем для каждой страницы: битовая карта (BITMAP_SIZE_BYTES) и данные (PAGE_DATA_SIZE)
        private long GetPageOffset(int pageNum)
        {
            return 2 + pageNum * (Constants.BITMAP_SIZE_BYTES + Constants.PAGE_DATA_SIZE);
        }

        // Чтение страницы из файла подкачки в объект page
        private void ReadPageFromDisk(int pageNum, Page page)
        {
            long offset = GetPageOffset(pageNum);
            swapFile.Seek(offset, SeekOrigin.Begin);

            // Читаем битовую карту
            byte[] bitmapBytes = new byte[Constants.BITMAP_SIZE_BYTES];
            int bytesRead = swapFile.Read(bitmapBytes, 0, Constants.BITMAP_SIZE_BYTES);
            if (bytesRead != Constants.BITMAP_SIZE_BYTES)
            {
                throw new Exception("Ошибка чтения битовой карты");
            }
            for (int i = 0; i < Constants.CELLS_PER_PAGE; i++)
            {
                int byteIndex = i / 8;
                int bitIndex = i % 8;
                page.Bitmap[i] = ((bitmapBytes[byteIndex] >> bitIndex) & 0x1) == 1;
            }

            // Читаем данные страницы (128 целых чисел, 512 байт)
            byte[] dataBytes = new byte[Constants.PAGE_DATA_SIZE];
            bytesRead = swapFile.Read(dataBytes, 0, Constants.PAGE_DATA_SIZE);
            if (bytesRead != Constants.PAGE_DATA_SIZE)
            {
                throw new Exception("Ошибка чтения данных страницы");
            }
            for (int i = 0; i < Constants.CELLS_PER_PAGE; i++)
            {
                page.Data[i] = BitConverter.ToInt32(dataBytes, i * Constants.INT_SIZE);
            }
            page.AbsPageNumber = pageNum;
            page.Modified = false;
            page.Valid = true;
            page.LastAccessTime = DateTime.Now;
        }

        // Запись страницы из памяти в файл подкачки
        private void WritePageToDisk(int pageNum, Page page)
        {
            long offset = GetPageOffset(pageNum);
            swapFile.Seek(offset, SeekOrigin.Begin);

            // Подготавливаем массив байт для битовой карты
            byte[] bitmapBytes = new byte[Constants.BITMAP_SIZE_BYTES];
            for (int i = 0; i < Constants.CELLS_PER_PAGE; i++)
            {
                int byteIndex = i / 8;
                int bitIndex = i % 8;
                if (page.Bitmap[i])
                {
                    bitmapBytes[byteIndex] |= (byte)(1 << bitIndex);
                }
            }
            swapFile.Write(bitmapBytes, 0, bitmapBytes.Length);

            // Подготавливаем массив байт для данных
            byte[] dataBytes = new byte[Constants.PAGE_DATA_SIZE];
            for (int i = 0; i < Constants.CELLS_PER_PAGE; i++)
            {
                byte[] intBytes = BitConverter.GetBytes(page.Data[i]);
                Buffer.BlockCopy(intBytes, 0, dataBytes, i * Constants.INT_SIZE, Constants.INT_SIZE);
            }
            swapFile.Write(dataBytes, 0, dataBytes.Length);
            swapFile.Flush();
            page.Modified = false;
        }

        // Метод для получения страницы, в которой находится элемент с заданным индексом.
        // Если нужная страница отсутствует в буфере, выбирается страница для замещения,
        // при необходимости производится сброс измененной страницы на диск, затем загружается новая страница.
        private (Page, int) GetPageForIndex(long index)
        {
            if (index >= arraySize)
                throw new IndexOutOfRangeException("Индекс выходит за границы массива");

            int pageNum = (int)(index / Constants.CELLS_PER_PAGE);
            int indexInPage = (int)(index % Constants.CELLS_PER_PAGE);

            // Проверяем, есть ли нужная страница в буфере
            foreach (var p in pageBuffer)
            {
                if (p.Valid && p.AbsPageNumber == pageNum)
                {
                    p.LastAccessTime = DateTime.Now;
                    return (p, indexInPage);
                }
            }

            // Если нет, выбираем страницу для замещения:
            Page replacePage = pageBuffer.FirstOrDefault(p => !p.Valid);
            if (replacePage == null)
            {
                // Если все заняты, выбираем самую старую страницу
                replacePage = pageBuffer.OrderBy(p => p.LastAccessTime).First();
                if (replacePage.Modified)
                {
                    WritePageToDisk(replacePage.AbsPageNumber, replacePage);
                }
            }
            // Загружаем нужную страницу из файла
            ReadPageFromDisk(pageNum, replacePage);
            return (replacePage, indexInPage);
        }

        // Метод чтения элемента виртуального массива по глобальному индексу
        public int ReadElement(long index)
        {
            var (page, indexInPage) = GetPageForIndex(index);
            // Если по данной ячейке ещё не записывалось значение – возвращаем 0 по умолчанию
            if (!page.Bitmap[indexInPage])
                return 0;
            return page.Data[indexInPage];
        }

        // Метод записи значения в элемент виртуального массива по глобальному индексу
        public void WriteElement(long index, int value)
        {
            var (page, indexInPage) = GetPageForIndex(index);
            page.Data[indexInPage] = value;
            page.Bitmap[indexInPage] = true;
            page.Modified = true;
            page.LastAccessTime = DateTime.Now;
        }

        // Метод закрытия файла подкачки (с предварительным сбросом модифицированных страниц)
        public void Close()
        {
            Flush();
            swapFile.Close();
        }
    }

    // Тестирующая программа – консольное приложение
    // Команды:
    //   Input <индекс> <значение> – запись значения в элемент массива
    //   Print <индекс> – вывод значения элемента массива
    //   Exit – завершение программы
    public class Program
    {
        public static void Main(string[] args)
        {
            try
            {
                Console.Write("VM> ");
                string command;

                // Создаем виртуальный массив целых чисел (например, размером 5000 элементов)
                VirtualIntArray vmIntArray = new VirtualIntArray("swapfile.dat", 5000);

                while ((command = Console.ReadLine()) != null)
                {
                    if (command.StartsWith("Input"))
                    {
                        // Формат команды: Input <индекс> <значение>
                        string[] parts = command.Split(' ', (char)StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length >= 3)
                        {
                            if (long.TryParse(parts[1], out long index) && int.TryParse(parts[2], out int value))
                            {
                                vmIntArray.WriteElement(index, value);
                                Console.WriteLine($"Записано: [{index}] = {value}");
                            }
                            else
                            {
                                Console.WriteLine("Неверный формат индекса или значения");
                            }
                        }
                        else
                        {
                            Console.WriteLine("Недостаточно параметров для команды Input");
                        }
                    }
                    else if (command.StartsWith("Print"))
                    {
                        // Формат команды: Print <индекс>
                        string[] parts = command.Split(' ', (char)StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length >= 2)
                        {
                            if (long.TryParse(parts[1], out long index))
                            {
                                int val = vmIntArray.ReadElement(index);
                                Console.WriteLine($"Элемент [{index}] = {val}");
                            }
                            else
                            {
                                Console.WriteLine("Неверный формат индекса");
                            }
                        }
                        else
                        {
                            Console.WriteLine("Недостаточно параметров для команды Print");
                        }
                    }
                    else if (command.StartsWith("Exit"))
                    {
                        break;
                    }
                    else
                    {
                        Console.WriteLine("Неизвестная команда.");
                    }
                    Console.Write("VM> ");
                }
                vmIntArray.Close();
            }
            catch (Exception ex)
            {
                Console.WriteLine("Ошибка: " + ex.Message);
            }
        }
    }
}
