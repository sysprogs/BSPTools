extern "C"
{
    char *getcwd(char *buf, int size)
    {
        if (size > 0)
            buf[0] = '/';
        if (size > 1)
            buf[1] = 0;
        return buf;
    }
    
    int mkdir()
    {
        return 1;
    }
}